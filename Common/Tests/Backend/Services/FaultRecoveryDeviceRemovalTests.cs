using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class FaultRecoveryDeviceRemovalTests
{
    [TestMethod]
    [Timeout(120_000)]
    [TestCategory("DeviceRemoval")]
    [TestCategory("EnrollmentSecurity")]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task TerminalFaultWithoutRetainedForkRow_CannotAuthorizeRemovalOfEitherRemoteDevice(
        bool selectOtherDevice, bool controlConflict)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var faulty = await ProductionSyncTestHost.CreateAsync();
        await using var healthy = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("removal_fault_boundary"));
        foreach (var target in new[] { faulty, healthy })
        {
            source.ConnectTo(target); target.ConnectTo(source);
            await source.EnableSyncAsync(token);
            var code = await target.Endpoints.StartDeviceEnrollmentAsync();
            await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        }
        var other = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("unrelated_fault_profile"));
        var otherBefore = await source.Endpoints.GetUserProfileInfoAsync(other);
        var originalOrigin = source.Identity.OriginInstanceId;
        Guid userId;
        int operationCount;
        byte[] canonicalBefore;
        using (var scope = source.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token);
            userId = user.UId;
            canonicalBefore = user.EncryptedPayload.ToArray();
            operationCount = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId)).Count;
            await scope.ServiceProvider.GetRequiredService<IUserSyncFaultService>().RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = userId,
                Scope = controlConflict ? UserSyncFaultScope.ControlPlane : UserSyncFaultScope.SnapshotFork,
                Kind = controlConflict ? UserSyncFaultKind.MembershipConflict : UserSyncFaultKind.SameRevisionFork,
                Status = UserSyncHealthStatus.TerminalConflict,
                OriginDeviceId = faulty.Identity.LocalDeviceId, OriginInstanceId = faulty.Identity.OriginInstanceId,
                KeyEpoch = user.KeyEpoch, MembershipEpoch = user.MembershipEpoch, OriginRevision = 7,
                ObservedHash = Enumerable.Repeat((byte)0x11, 32).ToArray(),
                ConflictingHash = Enumerable.Repeat((byte)0x22, 32).ToArray(),
                BlocksLogin = true, BlocksMerge = true, BlocksGarbageCollection = true
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        using (var scope = source.Services.CreateScope())
        {
            using var key = scope.ServiceProvider.GetRequiredService<IUserSessionService>().GetEncryptionKeyFromToken(token);
            Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<IUserSnapshotMergeCoordinator>()
                .TryMergePendingAsync(userId, key));
            var user = await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token);
            await Assert.ThrowsExactlyAsync<SyncStateUnavailableException>(() => scope.ServiceProvider
                .GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user));
            CollectionAssert.AreEqual(canonicalBefore, user.EncryptedPayload);
        }
        var password = Encoding.UTF8.GetBytes("P@ssw0rd12345678");
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => source.Endpoints.DisconnectUserDeviceAsync(
                token, selectOtherDevice ? healthy.Identity.LocalDeviceId : faulty.Identity.LocalDeviceId, password));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(password); }
        using var verify = source.Services.CreateScope();
        var memberships = await verify.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(userId);
        Assert.AreEqual(3, memberships.Count(row => row.IsActive));
        Assert.AreEqual(operationCount, (await verify.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
            .ListForUserAsync(userId)).Count);
        Assert.IsTrue(memberships.Any(row => row.IsActive && row.DeviceId == healthy.Identity.LocalDeviceId));
        Assert.IsTrue(memberships.Any(row => row.IsActive && row.DeviceId == faulty.Identity.LocalDeviceId));
        Assert.AreEqual(originalOrigin, source.Identity.OriginInstanceId);
        Assert.AreEqual(otherBefore.Username, (await source.Endpoints.GetUserProfileInfoAsync(other)).Username);
        Assert.AreEqual(UserSyncHealthStatus.TerminalConflict, (await verify.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>()
            .ListActiveForUserAsync(userId)).Single().Status);
    }
}
