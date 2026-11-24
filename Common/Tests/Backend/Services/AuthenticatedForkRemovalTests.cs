using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class AuthenticatedForkRemovalTests
{
    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("DeviceRemoval")]
    [TestCategory("EnrollmentSecurity")]
    [Timeout(180_000)]
    public async Task ExplicitRemoval_RetainsBothForksAndReceipts_ThenHealthyDevicesContinueAfterRestart() =>
        await ExerciseRemovalAsync(3);

    [TestMethod]
    [TestCategory("SyncStressExtended")]
    [TestCategory("DeviceRemoval")]
    [Timeout(300_000)]
    public async Task FiveDevices_DelayedDeliveryAndLockedRecipients_PreserveSignedExclusion()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run the five-device case.");
        await ExerciseRemovalAsync(5);
    }

    private static async Task ExerciseRemovalAsync(int deviceCount)
    {
        var hosts = new List<ProductionSyncTestHost>();
        try
        {
            for (var index = 0; index < deviceCount; index++) hosts.Add(await ProductionSyncTestHost.CreateAsync());
            var source = hosts[0]; var faulty = hosts[1];
            var (token, userId, first, second) = await PrepareAsync(hosts);
            byte[] passwordsBefore;
            using (var scope = source.Services.CreateScope())
                passwordsBefore = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!
                    .EncryptedUserPasswordsDataPayload.ToArray();
            var oldOrigin = faulty.Identity.OriginInstanceId;
            await source.Endpoints.LogoutAsync(token);
            token = await source.Endpoints.LoginAsync(source.CreateLoginRequest("fork_removal"));
            await Assert.ThrowsExactlyAsync<InvalidInputException>(() => source.Endpoints.DisconnectUserDeviceAsync(
                token, faulty.Identity.LocalDeviceId, Encoding.UTF8.GetBytes("WrongPassword123!")));
            await Assert.ThrowsExactlyAsync<SyncStateUnavailableException>(() => source.Endpoints.AddNewPasswordAsync(token,
                new PasswordManagerLocal.Common.Contracts.Requests.NewPasswordRequest
                { Name = "must-not-be-saved", Password = Encoding.UTF8.GetBytes("blocked-edit") }));
            var removed = await source.Endpoints.DisconnectUserDeviceAsync(token, faulty.Identity.LocalDeviceId, Password());
            Assert.IsTrue(removed.OperationId.HasValue, "A successful device removal must create a signed control operation.");
            var removalOperationId = removed.OperationId.Value;
            Assert.IsTrue(removed.Removed);
            Assert.IsFalse(removed.AllKnownOriginRevisionsMerged);
            UserControlOperationEnvelope operation;
            using (var scope = source.Services.CreateScope())
            {
                operation = UserControlOperationEnvelopeUtil.Deserialize((await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                    .GetByIdAsync(removalOperationId))!.EnvelopePayload);
                var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(operation.OperationPayload);
                Assert.IsNotNull(payload.ForkExclusion);
                AssertSignedProofCannotBeRebound(operation);
                Assert.IsTrue(payload.Origins.All(origin => origin.HighestAcceptedSnapshotRevision == 0 && origin.HighestAcceptedControlSequence == 0));
                CollectionAssert.AreEqual(first.SnapshotHash, DeviceRemovalForkProofUtil.Validate(payload).First.SnapshotHash);
                CollectionAssert.AreEqual(second.SnapshotHash, DeviceRemovalForkProofUtil.Validate(payload).Second.SnapshotHash);
                var retained = (await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId))
                    .Single(row => row.OriginDeviceId == faulty.Identity.LocalDeviceId);
                Assert.AreEqual(UserSyncSnapshotStatus.RetiredForkEvidence, retained.Status);
                Assert.IsNotNull(retained.ConflictingEnvelopePayload);
                var fault = (await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListForUserAsync(userId))
                    .Single(row => row.Scope == UserSyncFaultScope.SnapshotFork);
                Assert.AreEqual(UserSyncHealthStatus.RetiredOrigin, fault.Status);
                Assert.IsNull(fault.RecoveredAtUtc);
                Assert.IsNull(fault.SupersedingRevision);
                Assert.AreNotEqual(RevocationDeliveryState.Completed,
                    (await scope.ServiceProvider.GetRequiredService<IRevocationDeliveryService>().GetSnapshotsAsync())
                        .Single(item => item.OperationId == removalOperationId).State);
                Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(removalOperationId))
                    .Any(item => item.RecipientDeviceId == source.Identity.LocalDeviceId && item.Kind == RevocationReceiptKind.Applied));
                CollectionAssert.AreEqual(passwordsBefore, (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                    .GetByIdAsync(userId))!.EncryptedUserPasswordsDataPayload);
                Assert.AreEqual(UserSnapshotReceiptState.Rejected,
                    (await scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>().StoreAsync(first, hosts[2].Identity.LocalDeviceId)).State);
            }
            // Recipients are still locked. Membership/removal and Applied receipts must
            // commit before any password-derived key or subsequent ordinary snapshot.
            foreach (var host in hosts.Skip(2))
            {
                using var scope = host.Services.CreateScope();
                Assert.AreEqual(UserControlOperationReceiptState.Applied,
                    (await scope.ServiceProvider.GetRequiredService<IUserControlOperationInboxService>()
                        .StoreAndApplyAsync(operation, source.Identity.LocalDeviceId)).State);
                Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(removalOperationId))
                    .Any(item => item.RecipientDeviceId == host.Identity.LocalDeviceId && item.Kind == RevocationReceiptKind.Applied));
                Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().HasIsolatedForkAsync(userId));
                Assert.AreEqual(0, (await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(userId))
                    .Count(UserSyncFaultPolicy.IsTerminal));
            }
            // Return an authenticated, but contaminated, relay publication. The removal
            // exclusion applies to coverage too, not just to the original author's packets.
            using (var scope = source.Services.CreateScope())
            {
                var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
                var published = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user);
                var relayed = Clone(published);
                relayed.OriginDeviceId = hosts[2].Identity.LocalDeviceId;
                relayed.OriginInstanceId = hosts[2].Identity.OriginInstanceId;
                relayed.OriginRevision = 999;
                relayed.Coverage.Add(new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = faulty.Identity.LocalDeviceId, OriginInstanceId = faulty.Identity.OriginInstanceId,
                    UserKeyEpoch = 1, OriginRevision = first.OriginRevision
                });
                UserSnapshotEnvelopeUtil.FillOriginAuthentication(relayed, hosts[2].Identity);
                Assert.AreEqual(UserSnapshotReceiptState.Rejected,
                    (await scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>().StoreAsync(relayed, hosts[2].Identity.LocalDeviceId)).State);
            }
            foreach (var host in hosts.Skip(2))
            {
                var healthyToken = await host.Endpoints.LoginAsync(host.CreateLoginRequest("fork_removal"));
                Assert.AreEqual(userId, (await host.Endpoints.GetUserProfileInfoAsync(healthyToken)).UId);
            }
            hosts[0] = await source.RestartAsync();
            source = hosts[0];
            token = await source.Endpoints.LoginAsync(source.CreateLoginRequest("fork_removal"));
            Assert.AreEqual(userId, (await source.Endpoints.GetUserProfileInfoAsync(token)).UId);
            using (var scope = source.Services.CreateScope())
            {
                Assert.AreEqual(0, (await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(userId))
                    .Count(UserSyncFaultPolicy.IsTerminal));
                Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(removalOperationId));
            }
            await source.Endpoints.AddNewPasswordAsync(token, new PasswordManagerLocal.Common.Contracts.Requests.NewPasswordRequest
            { Name = "post-removal", Password = Encoding.UTF8.GetBytes("preserved-after-removal") });
            UserSyncSnapshot nextPublication;
            using (var scope = source.Services.CreateScope())
                nextPublication = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>()
                    .GetOrCreateAsync((await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!);
            foreach (var host in hosts.Skip(2))
            {
                using (var scope = host.Services.CreateScope())
                {
                    Assert.AreNotEqual(UserSnapshotReceiptState.Rejected,
                        (await scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>()
                            .StoreAsync(Clone(nextPublication), source.Identity.LocalDeviceId)).State);
                    var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
                    using var key = EncryptionKey.FromPassword(Password(), user.PasswordSalt);
                    await scope.ServiceProvider.GetRequiredService<IUserSnapshotMergeCoordinator>().TryMergePendingAsync(userId, key);
                }
                var healthyToken = await host.Endpoints.LoginAsync(host.CreateLoginRequest("fork_removal"));
                Assert.IsTrue((await host.Endpoints.GetSavedPasswordsAsync(healthyToken)).Passwords.Any(item => item.Name == "post-removal"));
            }
            // A genuinely new identity can join only through a fresh explicit +/code
            // workflow. Its bootstrap must retain the old fork and signed exclusion.
            var replacement = await ProductionSyncTestHost.CreateAsync(); hosts.Add(replacement);
            source.ConnectTo(replacement); replacement.ConnectTo(source);
            var enrollment = await replacement.Endpoints.StartDeviceEnrollmentAsync();
            await source.Endpoints.AddDeviceByCodeAsync(token, enrollment.Code);
            var replacementToken = await replacement.Endpoints.LoginAsync(replacement.CreateLoginRequest("fork_removal"));
            Assert.IsTrue((await replacement.Endpoints.GetSavedPasswordsAsync(replacementToken)).Passwords.Any(item => item.Name == "post-removal"));
            using (var scope = replacement.Services.CreateScope())
            {
                var retained = (await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId))
                    .Single(row => row.OriginDeviceId == faulty.Identity.LocalDeviceId);
                Assert.AreEqual(UserSyncSnapshotStatus.RetiredForkEvidence, retained.Status);
                Assert.IsNotNull(retained.ConflictingEnvelopePayload);
                Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(userId))
                    .Single(row => row.DeviceId == faulty.Identity.LocalDeviceId).IsActive);
            }
            Assert.AreEqual(oldOrigin, faulty.Identity.OriginInstanceId, "Removal must never rotate or replace the target identity.");
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) await host.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("DeviceRemoval")]
    [TestCategory("EnrollmentSecurity")]
    [DataRow("wrong-target")]
    [DataRow("missing-second-envelope")]
    [DataRow("merged-target")]
    [DataRow("control-conflict")]
    [DataRow("second-origin-fault")]
    [DataRow("missing-addition")]
    [DataRow("tampered-signature")]
    [DataRow("missing-checkpoint")]
    [DataRow("local-origin-fault")]
    [DataRow("key-epoch-conflict")]
    [Timeout(180_000)]
    public async Task UnprovenRemoval_LeavesAuthorityForksAndCanonicalDataUnchanged(string condition)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var faulty = await ProductionSyncTestHost.CreateAsync();
        await using var healthy = await ProductionSyncTestHost.CreateAsync();
        var (token, userId, _, _) = await PrepareAsync([source, faulty, healthy]);
        using (var scope = source.Services.CreateScope())
        {
            if (condition == "missing-second-envelope")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
                var row = (await repository.ListForUserAsync(userId)).Single(item => item.Status == UserSyncSnapshotStatus.IsolatedFork);
                row.ConflictingEnvelopePayload = null; repository.Update(row);
            }
            if (condition == "merged-target")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>();
                var row = (await repository.ListForUserAsync(userId)).Single(item => item.OriginDeviceId == faulty.Identity.LocalDeviceId);
                row.HighestMergedRevision = 1; repository.Update(row);
            }
            if (condition == "control-conflict")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserControlStateRepository>();
                var row = (await repository.GetAsync(userId))!; row.HasConflict = true; repository.Update(row);
            }
            if (condition == "missing-addition")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>();
                var addition = (await repository.ListForUserAsync(userId)).First(row => row.OperationType == UserControlOperationType.DeviceAddition);
                addition.Status = UserControlOperationStatus.Quarantined;
                repository.Update(addition);
            }
            if (condition == "tampered-signature")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
                var row = (await repository.ListForUserAsync(userId)).Single(item => item.Status == UserSyncSnapshotStatus.IsolatedFork);
                var competing = JsonSerializer.Deserialize(row.ConflictingEnvelopePayload!, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
                competing.OriginSignature[0] ^= 0x01;
                row.ConflictingEnvelopePayload = JsonSerializer.SerializeToUtf8Bytes(competing, BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
                repository.Update(row);
            }
            if (condition == "missing-checkpoint")
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUserCanonicalCheckpointRepository>();
                var checkpoint = (await repository.GetAsync(userId))!;
                checkpoint.Signature[0] ^= 0x01;
                repository.Update(checkpoint);
            }
            if (condition is "local-origin-fault" or "key-epoch-conflict")
                await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().RecordAsync(new UserSyncFaultDescriptor
                {
                    UserId = userId, Scope = condition == "local-origin-fault" ? UserSyncFaultScope.SnapshotFork : UserSyncFaultScope.ControlPlane,
                    Kind = condition == "local-origin-fault" ? UserSyncFaultKind.SameRevisionFork : UserSyncFaultKind.KeyEpochConflict,
                    Status = UserSyncHealthStatus.TerminalConflict, OriginDeviceId = source.Identity.LocalDeviceId,
                    OriginInstanceId = source.Identity.OriginInstanceId, KeyEpoch = 1, OriginRevision = 50, BlocksLogin = true
                });
            if (condition == "second-origin-fault")
                await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().RecordAsync(new UserSyncFaultDescriptor
                {
                    UserId = userId, Scope = UserSyncFaultScope.SnapshotFork, Kind = UserSyncFaultKind.SameRevisionFork,
                    Status = UserSyncHealthStatus.TerminalConflict, OriginDeviceId = healthy.Identity.LocalDeviceId,
                    OriginInstanceId = healthy.Identity.OriginInstanceId, KeyEpoch = 1, OriginRevision = 5, BlocksLogin = true
                });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var denied = false;
        try
        {
            await source.Endpoints.DisconnectUserDeviceAsync(
                token, condition == "wrong-target" ? healthy.Identity.LocalDeviceId : faulty.Identity.LocalDeviceId, Password());
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException or
                                       System.Security.Cryptography.CryptographicException) { denied = true; }
        Assert.IsTrue(denied, "Missing or contradictory proof must refuse the exceptional removal.");
        using var verify = source.Services.CreateScope();
        Assert.AreEqual(3, (await verify.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListForUserAsync(userId)).Count(item => item.IsActive));
        Assert.IsFalse((await verify.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
            .Any(item => item.OperationType == UserControlOperationType.DeviceRemoval));
        Assert.IsTrue(await verify.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().HasIsolatedForkAsync(userId));
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("DeviceRemoval")]
    [TestCategory("Transaction")]
    [Timeout(180_000)]
    public async Task FailureBeforeCommit_RollsBackRemovalRetirementAndReceipts_ThenAllowsExplicitRetry()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var faulty = await ProductionSyncTestHost.CreateAsync();
        await using var healthy = await ProductionSyncTestHost.CreateAsync();
        var (token, userId, first, second) = await PrepareAsync([source, faulty, healthy]);
        using (var scope = source.Services.CreateScope())
        {
            var failing = ActivatorUtilities.CreateInstance<PasswordManagerLocal.Common.Backend.Services.UserDeviceDisconnectionService>(
                scope.ServiceProvider, new FailCommitUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>()));
            await Assert.ThrowsExactlyAsync<IOException>(() => failing.DisconnectUserDeviceAsync(token, faulty.Identity.LocalDeviceId, Password()));
        }
        using (var scope = source.Services.CreateScope())
        {
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(userId))
                .Single(row => row.DeviceId == faulty.Identity.LocalDeviceId).IsActive);
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                .Any(row => row.OperationType == UserControlOperationType.DeviceRemoval));
            var retained = (await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId))
                .Single(row => row.Status == UserSyncSnapshotStatus.IsolatedFork);
            CollectionAssert.AreEqual(first.SnapshotHash, retained.SnapshotHash);
            Assert.IsNotNull(retained.ConflictingEnvelopePayload);
            CollectionAssert.AreEqual(second.SnapshotHash, retained.ConflictingSnapshotHash!);
            Assert.AreEqual(UserSyncHealthStatus.TerminalConflict,
                (await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(userId)).Single().Status);
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListEvidenceAsync())
                .Any(row => UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload).OperationType == UserControlOperationType.DeviceRemoval));
        }
        Assert.IsTrue((await source.Endpoints.DisconnectUserDeviceAsync(token, faulty.Identity.LocalDeviceId, Password())).Removed);
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("DeviceRemoval")]
    [TestCategory("EnrollmentSecurity")]
    [Timeout(180_000)]
    public async Task RecipientThatMergedTheExcludedOrigin_AppliesRevocationButKeepsUniqueDataAndTerminalBlock()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var faulty = await ProductionSyncTestHost.CreateAsync();
        await using var recipient = await ProductionSyncTestHost.CreateAsync();
        var (token, userId, first, _) = await PrepareAsync([source, faulty, recipient]);
        byte[] canonicalBefore;
        using (var scope = recipient.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>().StoreAsync(first, faulty.Identity.LocalDeviceId);
            var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
            using var key = EncryptionKey.FromPassword(Password(), user.PasswordSalt);
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IUserSnapshotMergeCoordinator>().TryMergePendingAsync(userId, key));
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ClearTrackedChanges();
            canonicalBefore = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!.EncryptedUserPasswordsDataPayload.ToArray();
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
                .GetAsync(userId, faulty.Identity.LocalDeviceId, faulty.Identity.OriginInstanceId, 1))!.HighestMergedRevision > 0);
        }
        var removed = await source.Endpoints.DisconnectUserDeviceAsync(token, faulty.Identity.LocalDeviceId, Password());
        Assert.IsTrue(removed.OperationId.HasValue, "A successful device removal must create a signed control operation.");
        var removalOperationId = removed.OperationId.Value;
        UserControlOperationEnvelope operation;
        using (var scope = source.Services.CreateScope())
            operation = UserControlOperationEnvelopeUtil.Deserialize((await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                .GetByIdAsync(removalOperationId))!.EnvelopePayload);
        using (var scope = recipient.Services.CreateScope())
        {
            Assert.AreEqual(UserControlOperationReceiptState.Applied,
                (await scope.ServiceProvider.GetRequiredService<IUserControlOperationInboxService>()
                    .StoreAndApplyAsync(operation, source.Identity.LocalDeviceId)).State);
            var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
            CollectionAssert.AreEqual(canonicalBefore, user.EncryptedUserPasswordsDataPayload,
                "Revocation must not replace or discard potentially unique merged data.");
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().HasTerminalFaultAsync(userId));
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().HasIsolatedForkAsync(userId));
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(userId))
                .Single(row => row.DeviceId == faulty.Identity.LocalDeviceId).IsActive);
            await Assert.ThrowsExactlyAsync<SyncStateUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user));
        }
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => recipient.Endpoints.LoginAsync(recipient.CreateLoginRequest("fork_removal")));
    }

    private sealed class FailCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
            new FailCommitTransaction(await inner.BeginTransactionAsync(ct));
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => inner.SaveChangesAsync(ct);
        public void ClearTrackedChanges() => inner.ClearTrackedChanges();
    }
    private sealed class FailCommitTransaction(IUnitOfWorkTransaction inner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => throw new IOException("Injected failure before commit.");
        public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<(Guid Token, Guid UserId, UserSnapshotEnvelope First, UserSnapshotEnvelope Second)> PrepareAsync(
        IReadOnlyList<ProductionSyncTestHost> hosts)
    {
        var source = hosts[0]; var faulty = hosts[1];
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("fork_removal"));
        foreach (var host in hosts.Skip(1))
        {
            source.ConnectTo(host); host.ConnectTo(source);
            await source.EnableSyncAsync(token);
            var code = await host.Endpoints.StartDeviceEnrollmentAsync();
            await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        }
        Guid userId; UserControlOperationEnvelope[] history; UserSyncSnapshot baseline;
        using (var scope = source.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token);
            userId = user.UId;
            history = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                .OrderBy(item => item.PreviousMembershipEpoch).Select(item => UserControlOperationEnvelopeUtil.Deserialize(item.EnvelopePayload)).ToArray();
            baseline = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user);
        }
        foreach (var host in hosts.Skip(1))
        {
            using var scope = host.Services.CreateScope();
            foreach (var operation in history)
                await scope.ServiceProvider.GetRequiredService<IUserControlOperationInboxService>().StoreAndApplyAsync(operation, source.Identity.LocalDeviceId);
            await scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>().StoreAsync(Clone(baseline), source.Identity.LocalDeviceId);
            var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
            using var key = EncryptionKey.FromPassword(Password(), user.PasswordSalt);
            await scope.ServiceProvider.GetRequiredService<IUserSnapshotMergeCoordinator>().TryMergePendingAsync(userId, key);
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ClearTrackedChanges();
            user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
            await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAfterRecoveryAsync(user, key, UserSyncKeyConfidence.ExplicitlyTrusted);
        }
        var first = Clone(baseline);
        first.OriginDeviceId = faulty.Identity.LocalDeviceId; first.OriginInstanceId = faulty.Identity.OriginInstanceId;
        first.OriginRevision = 5;
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(first, faulty.Identity);
        var second = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(first, BackendJsonSerializerContext.Default.UserSnapshotEnvelope),
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
        second.User.EncryptedUserPasswordsDataPayload[0] ^= 0x55;
        second.User.IntegrityHash = SyncCryptoUtil.CalculateUserHash(second.User, second.CreatedAtUtc.ToUnixTimeMilliseconds());
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(second, faulty.Identity);
        using (var scope = source.Services.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>();
            Assert.AreEqual(UserSnapshotReceiptState.StoredPending, (await inbox.StoreAsync(first, hosts[2].Identity.LocalDeviceId)).State);
            Assert.AreEqual(UserSnapshotReceiptState.Quarantined, (await inbox.StoreAsync(second, hosts[2].Identity.LocalDeviceId)).State);
        }
        return (token, userId, first, second);
    }

    private static void AssertSignedProofCannotBeRebound(UserControlOperationEnvelope operation)
    {
        var modified = UserControlOperationEnvelopeUtil.Deserialize(UserControlOperationEnvelopeUtil.Serialize(operation));
        var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(modified.OperationPayload);
        var proof = payload.ForkExclusion!;
        (proof.FirstEnvelope, proof.SecondEnvelope) = (proof.SecondEnvelope, proof.FirstEnvelope);
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
        modified.OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload);
        Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.ValidateStructureAndHash(modified),
            "Even reordering valid originals changes the signed removal identity.");
        proof.AuthorizationId = Guid.NewGuid();
        Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload),
            "The same originals cannot be assigned to a different authorization.");
    }

    private static UserSnapshotEnvelope Clone(UserSyncSnapshot snapshot) =>
        JsonSerializer.Deserialize(snapshot.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
    private static byte[] Password() => Encoding.UTF8.GetBytes("P@ssw0rd12345678");
}
