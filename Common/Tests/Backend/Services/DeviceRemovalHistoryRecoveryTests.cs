using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class DeviceRemovalHistoryRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Disconnect_BlockedAndUnblockedPeersLeaveValidHistoryAndAllowAnotherEnrollment(bool blocked)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        await using var next = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("removal_state"));
        await EnrollAsync(source, removed, token);
        using (var scope = source.Services.CreateScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var device = (await devices.GetByIdAsync(removed.Identity.LocalDeviceId))!;
            device.IsBlocked = blocked;
            device.BlockedReason = blocked ? "invalid device removal history" : null;
            device.BlockedAt = blocked ? DateTimeOffset.UtcNow : null;
            devices.Update(device);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await DisconnectAsync(source, removed, token);
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(0, result.RepairedCount, "Ordinary removal must produce valid evidence without a repair.");
        await EnrollAsync(source, next, token);
        var nextToken = await next.Endpoints.LoginAsync(next.CreateLoginRequest("removal_state"));
        Assert.IsTrue((await next.Endpoints.RunSelfDiagnosticsAndRepairAsync(nextToken)).Healthy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DamagedCutoffProjection_IsRebuiltWithoutResettingHistoryWhenAnotherPeerRemains(bool missing)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        await using var remaining = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("rebuild_cutoff"));
        await EnrollAsync(source, removed, token);
        await EnrollAsync(source, remaining, token);
        await DisconnectAsync(source, removed, token);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var sql = missing
                ? """DELETE FROM "UserOriginRemovalCutoffs" WHERE "UserId" = $user;"""
                : """
                  UPDATE "UserOriginRemovalCutoffs" SET "HighestAcceptedSnapshotRevision" = "HighestAcceptedSnapshotRevision" + 100
                  WHERE "UserId" = $user;
                  """;
            Assert.AreEqual(1, await scope.ServiceProvider.ExecuteFixtureSqlAsync(sql, ("$user", before.UId)));
        }

        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        CollectionAssert.Contains(result.Findings.ToArray(), "RemovalCutoffsRebuilt");
        CollectionAssert.DoesNotContain(result.Findings.ToArray(), "SingleDeviceHistoryReset");
        var after = await ReadUserAsync(source, token);
        Assert.AreEqual(before.KeyEpoch, after.KeyEpoch);
        Assert.AreEqual(before.MembershipEpoch, after.MembershipEpoch);
        using var verify = source.Services.CreateScope();
        var active = await verify.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListActiveForDeviceAsync(before.UId, remaining.Identity.LocalDeviceId);
        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(0, (await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token)).RepairedCount);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task UnrecoverableLocalOnlyHistory_ResetPreservesSecretsSecondProfileAndIdentityAndAllowsEnrollment()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        await using var next = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("reset_history"));
        var otherToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("unaffected_profile"));
        await AddSecretAsync(source, token, "Preserve this secret");
        await AddSecretAsync(source, otherToken, "Other profile secret");
        await EnrollAsync(source, removed, token);
        await EnrollAsync(source, removed, otherToken);
        await DisconnectAsync(source, removed, token);
        var before = await ReadUserAsync(source, token);
        var otherBefore = await ReadUserAsync(source, otherToken);
        UserControlOperationEnvelope oldAddition;
        using (var scope = source.Services.CreateScope())
        {
            var old = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                .ListForUserAsync(before.UId)).Single(operation => operation.OperationType == UserControlOperationType.DeviceAddition);
            oldAddition = UserControlOperationEnvelopeUtil.Deserialize(old.EnvelopePayload);
        }
        var deviceId = source.Identity.LocalDeviceId;
        var originId = source.Identity.OriginInstanceId;
        await CorruptRemovalAsync(source, before.UId);
        using (var scope = source.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = before.UId, Scope = UserSyncFaultScope.ControlPlane,
                Kind = UserSyncFaultKind.MembershipConflict, Status = UserSyncHealthStatus.TerminalConflict,
                KeyEpoch = before.KeyEpoch, MembershipEpoch = before.MembershipEpoch,
                DiagnosticCode = "invalid-removal-history", BlocksLifecycle = true
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        CollectionAssert.Contains(result.Findings.ToArray(), "SingleDeviceHistoryReset");
        var after = await ReadUserAsync(source, token);
        Assert.IsTrue(after.KeyEpoch > before.KeyEpoch);
        Assert.AreEqual(1L, after.MembershipEpoch);
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, after.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(before.EncryptedGeneralUserDataPayload, after.EncryptedGeneralUserDataPayload);
        CollectionAssert.AreEqual(before.EncryptedPayload, after.EncryptedPayload);
        CollectionAssert.AreEqual(otherBefore.IntegrityHash, (await ReadUserAsync(source, otherToken)).IntegrityHash);
        Assert.AreEqual(deviceId, source.Identity.LocalDeviceId);
        Assert.AreEqual(originId, source.Identity.OriginInstanceId);
        await AssertSecretAsync(source, token, "Preserve this secret");
        await AssertSecretAsync(source, otherToken, "Other profile secret");
        Assert.AreEqual(0, (await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token)).RepairedCount);
        await EnrollAsync(source, next, token);
        var nextToken = await next.Endpoints.LoginAsync(next.CreateLoginRequest("reset_history"));
        await AssertSecretAsync(next, nextToken, "Preserve this secret");
        Assert.IsTrue((await next.Endpoints.RunSelfDiagnosticsAndRepairAsync(nextToken)).Healthy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task InvalidSignedHistory_IsNotResetWhileAnotherPeerExistsEvenWhenBlockedOrSyncDisabled(bool blocked)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        await using var remaining = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("no_unsafe_reset"));
        await EnrollAsync(source, removed, token);
        await EnrollAsync(source, remaining, token);
        await DisconnectAsync(source, removed, token);
        var before = await ReadUserAsync(source, token);
        await CorruptRemovalAsync(source, before.UId);
        using (var scope = source.Services.CreateScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var device = (await devices.GetByIdAsync(remaining.Identity.LocalDeviceId))!;
            device.IsBlocked = blocked;
            devices.Update(device);
            var links = scope.ServiceProvider.GetRequiredService<IUserDeviceRepository>();
            var link = (await links.GetAsync(before.UId, device.Id))!;
            link.IsSyncOn = false;
            links.Update(link);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(result.Healthy);
        Assert.AreEqual(0, result.RepairedCount);
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ResetCommitFailure_RollsBackProfileHistoryAndCheckpointTogether()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("reset_rollback"));
        await EnrollAsync(source, removed, token);
        await DisconnectAsync(source, removed, token);
        var before = await ReadUserAsync(source, token);
        await CorruptRemovalAsync(source, before.UId);
        using (var scope = source.Services.CreateScope())
        {
            var diagnostics = ActivatorUtilities.CreateInstance<SelfDiagnosticsService>(scope.ServiceProvider,
                new FailCommitUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>()));
            await Assert.ThrowsExactlyAsync<IOException>(() => diagnostics.RunAsync(token));
        }
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        using (var scope = source.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var operations = await services.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(before.UId);
            Assert.IsTrue(operations.Any(row => row.OperationType == UserControlOperationType.DeviceRemoval));
            Assert.IsNotEmpty(await services.GetRequiredService<IUserOriginRemovalCutoffRepository>().ListForUserAsync(before.UId));
            Assert.HasCount(2, await services.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(before.UId));
        }
        // A clean retry must still pass the original checkpoint and repair successfully.
        Assert.IsTrue((await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token)).Healthy);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task UnmergedSnapshotKnowledge_PreventsDestructiveHistoryReset()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("keep_unmerged"));
        await EnrollAsync(source, removed, token);
        await DisconnectAsync(source, removed, token);
        var before = await ReadUserAsync(source, token);
        await CorruptRemovalAsync(source, before.UId);
        using (var scope = source.Services.CreateScope())
        {
            var revisions = scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>();
            var knowledge = (await revisions.ListForUserAsync(before.UId)).First();
            knowledge.HighestStoredRevision = knowledge.HighestMergedRevision + 1;
            revisions.Update(knowledge);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(result.Healthy);
        Assert.AreEqual(0, result.RepairedCount);
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SoleSurvivingJoinedInstallation_CanReplaceItsRemovedGenesisHistory()
    {
        await using var original = await ProductionSyncTestHost.CreateAsync();
        await using var survivor = await ProductionSyncTestHost.CreateAsync();
        var originalToken = await original.Endpoints.RegisterAsync(original.CreateRegistrationRequest("surviving_joined"));
        await AddSecretAsync(original, originalToken, "Surviving secret");
        await EnrollAsync(original, survivor, originalToken);
        var token = await survivor.Endpoints.LoginAsync(survivor.CreateLoginRequest("surviving_joined"));
        await DisconnectAsync(survivor, original, token);
        var before = await ReadUserAsync(survivor, token);
        await CorruptRemovalAsync(survivor, before.UId);

        var result = await survivor.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        CollectionAssert.Contains(result.Findings.ToArray(), "SingleDeviceHistoryReset");
        using var scope = survivor.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListForUserAsync(before.UId);
        Assert.AreEqual(1, rows.Count);
        Assert.IsTrue(rows[0].IsGenesis);
        Assert.AreEqual(survivor.Identity.LocalDeviceId, rows[0].DeviceId);
        await AssertSecretAsync(survivor, token, "Surviving secret");
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task PublicRemovalWriter_FailureDoesNotPersistAnAppliedOperationOrAdvanceItsSequence()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var remote = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("writer_rollback"));
        await EnrollAsync(source, remote, token);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var authorizations = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
            var authorization = (await authorizations.ListActiveForDeviceAsync(before.UId, remote.Identity.LocalDeviceId)).Single();
            var states = services.GetRequiredService<IUserControlStateRepository>();
            var nextSequence = (await states.GetAsync(before.UId))!.NextOriginSequence;
            var cutoffs = services.GetRequiredService<IUserOriginRemovalCutoffRepository>();
            await cutoffs.AddAsync(new UserOriginRemovalCutoff
            {
                UserId = before.UId, DeviceId = authorization.DeviceId,
                OriginInstanceId = authorization.OriginInstanceId, AuthorizationId = authorization.AuthorizationId,
                UserKeyEpoch = before.KeyEpoch, ResultingMembershipEpoch = before.MembershipEpoch + 1,
                RemovalOperationId = Guid.NewGuid(), RemovalOperationHash = new byte[32]
            });
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            var payload = new DeviceRemovalPayload
            {
                UserId = before.UId, RemovedDeviceId = authorization.DeviceId, KeyEpoch = before.KeyEpoch,
                PreviousMembershipEpoch = before.MembershipEpoch, ResultingMembershipEpoch = before.MembershipEpoch + 1,
                Origins = [new DeviceRemovalOriginCutoffPayload
                {
                    AuthorizationId = authorization.AuthorizationId, OriginInstanceId = authorization.OriginInstanceId,
                    UserKeyEpoch = before.KeyEpoch, SignPublicKeyHash = authorization.SignPublicKeyHash.ToArray(),
                    AdditionOperationId = authorization.AdditionOperationId,
                    AdditionOperationHash = authorization.AdditionOperationHash!.ToArray()
                }]
            };
            UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<IUserControlOperationWriterService>().CreateAppliedDeviceRemovalAsync(before, payload));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            using var verification = source.Services.CreateScope();
            var durable = verification.ServiceProvider;
            Assert.AreEqual(nextSequence, (await durable.GetRequiredService<IUserControlStateRepository>().GetAsync(before.UId))!.NextOriginSequence);
            var operations = await durable.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(before.UId);
            Assert.IsFalse(operations.Any(row => row.OperationType == UserControlOperationType.DeviceRemoval));
            Assert.IsTrue((await durable.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .GetByIdAsync(authorization.AuthorizationId))!.IsActive);
        }
    }

    private static async Task EnrollAsync(ProductionSyncTestHost source, ProductionSyncTestHost target, Guid token)
    {
        source.ConnectTo(target);
        target.ConnectTo(source);
        await source.EnableSyncAsync(token);
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
    }

    private static async Task DisconnectAsync(ProductionSyncTestHost source, ProductionSyncTestHost target, Guid token)
    {
        var result = await source.Endpoints.DisconnectUserDeviceAsync(token,
            target.Identity.LocalDeviceId, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        Assert.IsTrue(result.Removed);
    }

    private static async Task<User> ReadUserAsync(ProductionSyncTestHost host, Guid token)
    {
        using var scope = host.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token);
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(user.UId))!;
    }

    private static async Task CorruptRemovalAsync(ProductionSyncTestHost host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>();
        var operation = (await operations.ListForUserAsync(userId)).Single(row => row.OperationType == UserControlOperationType.DeviceRemoval);
        operation.EnvelopePayload = Encoding.UTF8.GetBytes("{malformed history");
        operations.Update(operation);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static Task AddSecretAsync(ProductionSyncTestHost host, Guid token, string name) =>
        host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = name, Password = Encoding.UTF8.GetBytes("RetainedSecret123!")
        });

    private static async Task AssertSecretAsync(ProductionSyncTestHost host, Guid token, string name)
    {
        var saved = (await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Single();
        Assert.AreEqual(name, saved.Name);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("RetainedSecret123!"),
            await host.Endpoints.GetUnsecurePasswordAsync(token, saved.Id));
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
}
