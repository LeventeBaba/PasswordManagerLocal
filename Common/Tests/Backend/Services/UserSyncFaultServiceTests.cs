using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSyncFaultServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task OriginFault_IsDurableScopedAndSupersededInsteadOfDeleted()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = new User { UId = Guid.NewGuid(), KeyEpoch = 1, MembershipEpoch = 1 };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();

        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var service = new UserSyncFaultService(database.UserSyncFaults);
        await service.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.SnapshotOrigin,
            Kind = UserSyncFaultKind.IncomingIntegrityFailure,
            Status = UserSyncHealthStatus.Isolated,
            AffectedComponent = "General",
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            KeyEpoch = 1,
            MembershipEpoch = 1,
            OriginRevision = 5,
            ObservedHash = Enumerable.Repeat((byte)0x55, 32).ToArray(),
            DiagnosticCode = "general-integrity-failed",
            BlocksMerge = true
        });
        await database.UnitOfWork.SaveChangesAsync();

        await service.MarkOriginRecoveredAsync(user.UId, originDeviceId, originInstanceId, 1, 6);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var faults = await database.UserSyncFaults.ListForUserAsync(user.UId);
        MSTestAssert.HasCount(1, faults);
        MSTestAssert.AreEqual(UserSyncHealthStatus.Superseded, faults[0].Status);
        MSTestAssert.AreEqual(6L, faults[0].SupersedingRevision);
        MSTestAssert.IsFalse(faults[0].BlocksMerge);
        MSTestAssert.IsNotNull(faults[0].RecoveredAtUtc);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TerminalFork_RemainsActiveWhenOrdinaryOriginFaultIsRecovered()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = new User { UId = Guid.NewGuid(), KeyEpoch = 1, MembershipEpoch = 1 };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();

        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var service = new UserSyncFaultService(database.UserSyncFaults);
        await service.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.SnapshotFork,
            Kind = UserSyncFaultKind.SameRevisionFork,
            Status = UserSyncHealthStatus.TerminalConflict,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            KeyEpoch = 1,
            OriginRevision = 5,
            DiagnosticCode = "same-revision-fork",
            BlocksMerge = true
        });
        await database.UnitOfWork.SaveChangesAsync();

        await service.MarkOriginRecoveredAsync(user.UId, originDeviceId, originInstanceId, 1, 6);
        await database.UnitOfWork.SaveChangesAsync();

        MSTestAssert.IsTrue(await service.HasTerminalOriginFaultAsync(
            user.UId, originDeviceId, originInstanceId, 1));
    }
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DifferentFaultKinds_InSameScopeAndEpoch_RemainIndependent()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = new User { UId = Guid.NewGuid(), KeyEpoch = 1, MembershipEpoch = 1 };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();

        var service = new UserSyncFaultService(database.UserSyncFaults);
        await service.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalIntegrityMismatch,
            Status = UserSyncHealthStatus.Isolated,
            KeyEpoch = 1,
            DiagnosticCode = "canonical-row-integrity-mismatch",
            BlocksPublishing = true,
            BlocksLogin = true
        });
        await service.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalKeyVerificationPending,
            Status = UserSyncHealthStatus.AwaitingEvidence,
            KeyEpoch = 1,
            DiagnosticCode = "membership-transition-awaiting-keyed-verification",
            BlocksPublishing = true
        });
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var active = await database.UserSyncFaults.ListActiveForUserAsync(user.UId);
        MSTestAssert.HasCount(2, active);
        CollectionAssert.AreEquivalent(
            new[]
            {
                UserSyncFaultKind.CanonicalIntegrityMismatch,
                UserSyncFaultKind.CanonicalKeyVerificationPending
            },
            active.Select(fault => fault.Kind).ToArray());
        MSTestAssert.IsTrue(active.Single(fault =>
            fault.Kind == UserSyncFaultKind.CanonicalIntegrityMismatch).BlocksLogin);
    }

}
