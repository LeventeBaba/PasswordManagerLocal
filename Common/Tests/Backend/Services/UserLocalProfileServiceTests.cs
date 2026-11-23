using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class UserLocalProfileServiceTests
{
    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("LocalProfileCleanup")]
    [TestCategory("EnrollmentSecurity")]
    [Timeout(180_000)]
    public async Task ExplicitCleanup_AfterSignedRemoval_ErasesOnlyLocalCopyAndRetainsAuthorityAndReceipts()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("cleanup_profile"));
        source.ConnectTo(target); target.ConnectTo(source);
        await source.EnableSyncAsync(token);
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        var targetToken = await target.Endpoints.LoginAsync(target.CreateLoginRequest("cleanup_profile"));
        var userId = (await target.Endpoints.GetUserProfileInfoAsync(targetToken)).UId;
        var otherToken = await target.Endpoints.RegisterAsync(target.CreateRegistrationRequest("cleanup_unaffected"));
        var otherId = (await target.Endpoints.GetUserProfileInfoAsync(otherToken)).UId;
        var originalDevice = target.Identity.LocalDeviceId;
        var originalOrigin = target.Identity.OriginInstanceId;
        await Assert.ThrowsExactlyAsync<InvalidInputException>(() => target.Endpoints.RemoveLocalProfileAsync(Request(), false));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => target.Endpoints.RemoveLocalProfileAsync(Request(), true));
        var removal = await source.Endpoints.DisconnectUserDeviceAsync(token, target.Identity.LocalDeviceId, Password());
        Assert.IsTrue(removal.OperationId.HasValue, "A successful device removal must create a signed control operation.");
        var removalOperationId = removal.OperationId.Value;
        UserControlOperationEnvelope operation;
        using (var scope = source.Services.CreateScope())
            operation = UserControlOperationEnvelopeUtil.Deserialize((await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                .GetByIdAsync(removalOperationId))!.EnvelopePayload);
        using (var scope = target.Services.CreateScope())
            Assert.AreEqual(UserControlOperationReceiptState.Applied,
                (await scope.ServiceProvider.GetRequiredService<IUserControlOperationInboxService>()
                    .StoreAndApplyAsync(operation, source.Identity.LocalDeviceId)).State);
        await Assert.ThrowsExactlyAsync<InvalidInputException>(() => target.Endpoints.RemoveLocalProfileAsync(
            new LoginRequest { Username = "cleanup_profile", Password = Encoding.UTF8.GetBytes("WrongPassword123!") }, true));
        // Explicit local erasure must retain even uncertain original fork evidence;
        // an ordinary signed removal does not necessarily embed that evidence itself.
        byte[] retainedOriginal; byte[] retainedCompeting;
        using (var scope = target.Services.CreateScope())
        {
            var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
            var row = (await snapshots.ListForUserAsync(userId)).First();
            retainedOriginal = row.EnvelopePayload.ToArray(); retainedCompeting = [0x51, 0x52];
            row.Status = UserSyncSnapshotStatus.IsolatedFork;
            row.ConflictingEnvelopePayload = retainedCompeting.ToArray();
            row.ConflictingSnapshotHash = new byte[32];
            snapshots.Update(row);
            await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = userId, Scope = UserSyncFaultScope.SnapshotFork, Kind = UserSyncFaultKind.SameRevisionFork,
                Status = UserSyncHealthStatus.TerminalConflict, OriginDeviceId = row.OriginDeviceId,
                OriginInstanceId = row.OriginInstanceId, KeyEpoch = row.UserKeyEpoch, OriginRevision = row.OriginRevision,
                ObservedHash = row.SnapshotHash.ToArray(), ConflictingHash = row.ConflictingSnapshotHash.ToArray(), BlocksLogin = true
            });
            await scope.ServiceProvider.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.Persistence.IUnitOfWork>().SaveChangesAsync();
        }
        byte[][] receipts;
        using (var scope = target.Services.CreateScope())
            receipts = (await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(removalOperationId))
                .Select(row => row.Payload.ToArray()).ToArray();
        await target.Endpoints.RemoveLocalProfileAsync(Request(), true);
        using (var scope = target.Services.CreateScope())
        {
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(otherId));
            Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().ExistsAsync(userId));
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>()
                .ListRecoverableLocalCanonicalAsync(DateTimeOffset.UtcNow.AddDays(1))).Any(row => row.UserId == userId),
                "Retained evidence without a mutable profile must not schedule recovery.");
            var fork = (await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId))
                .Single(row => row.Status == UserSyncSnapshotStatus.IsolatedFork);
            CollectionAssert.AreEqual(retainedOriginal, fork.EnvelopePayload);
            CollectionAssert.AreEqual(retainedCompeting, fork.ConflictingEnvelopePayload!);
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListForUserAsync(userId))
                .Any(row => row.Kind == UserSyncFaultKind.SameRevisionFork && row.Status == UserSyncHealthStatus.TerminalConflict));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(removalOperationId));
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListForUserAsync(userId)).Single(row => row.DeviceId == target.Identity.LocalDeviceId).IsActive);
            var after = await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(removalOperationId);
            Assert.AreEqual(receipts.Length, after.Count);
            foreach (var bytes in receipts) Assert.IsTrue(after.Any(row => row.Payload.SequenceEqual(bytes)));
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                .Any(row => row.OperationType == UserControlOperationType.AccountDeletion));
        }
        Assert.AreEqual(originalDevice, target.Identity.LocalDeviceId);
        Assert.AreEqual(originalOrigin, target.Identity.OriginInstanceId);
        Assert.AreEqual(otherId, (await target.Endpoints.GetUserProfileInfoAsync(otherToken)).UId);
        Assert.AreEqual(userId, (await source.Endpoints.GetUserProfileInfoAsync(token)).UId);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => target.Endpoints.LoginAsync(target.CreateLoginRequest("cleanup_profile")));
        var restart = await target.RestartAsync();
        try
        {
            using var scope = restart.Services.CreateScope();
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(otherId));
            Assert.AreEqual(originalOrigin, restart.Identity.OriginInstanceId);
            source.ConnectTo(restart); restart.ConnectTo(source);
            using var sourceScope = source.Services.CreateScope();
            var commits = sourceScope.ServiceProvider.GetRequiredService<IDeviceEnrollmentCommitRepository>();
            var originalCommit = (await commits.ListForUserAsync(userId))
                .Single(row => row.TargetDeviceId == originalDevice && row.TargetOriginInstanceId == originalOrigin);
            Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, originalCommit.Status);
            var originalCommitVersion = originalCommit.Version;
            var retry = await restart.Endpoints.StartDeviceEnrollmentAsync();
            var denied = await Assert.ThrowsExactlyAsync<PasswordManagerLocal.Common.Contracts.Errors.DeviceEnrollmentException>(
                () => source.Endpoints.AddDeviceByCodeAsync(token, retry.Code));
            Assert.AreEqual(PasswordManagerLocal.Common.Contracts.Enrollment.DeviceEnrollmentErrorCode.NewDeviceRejected, denied.ErrorCode);
            Assert.IsTrue(denied.IsKnownNotCommitted, "A revoked installation must be rejected without a new enrollment commit.");
            var afterDenied = (await commits.ListForUserAsync(userId))
                .Single(row => row.TargetDeviceId == originalDevice && row.TargetOriginInstanceId == originalOrigin);
            Assert.AreEqual(originalCommit.CommitId, afterDenied.CommitId);
            Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, afterDenied.Status,
                "The failed retry must not re-open a previously completed enrollment commit.");
            Assert.AreEqual(originalCommitVersion, afterDenied.Version);
            Assert.IsNull(await sourceScope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .GetActiveAsync(userId, originalDevice, originalOrigin),
                "A previously removed installation must remain unauthorized after an explicit retry.");
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId),
                "A removed identity must not inherit its previous account authority.");
        }
        finally { await restart.DisposeAsync(); }
    }

    private static LoginRequest Request() => new() { Username = "cleanup_profile", Password = Password() };
    private static byte[] Password() => Encoding.UTF8.GetBytes("P@ssw0rd12345678");
}
