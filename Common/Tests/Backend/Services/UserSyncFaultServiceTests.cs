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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
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
    [TestCategory("Recovery")]
    [TestCategory("Security")]
    public async Task TerminalCanonicalFault_CannotBeDowngradedOrMarkedRecovered_AndKeepsOriginalEvidence()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var service = new UserSyncFaultService(database.UserSyncFaults);
        var userId = (await CreateFaultUserAsync(database)).UId;
        var descriptor = new UserSyncFaultDescriptor
        {
            UserId = userId, Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalPasswordsBlobFailure,
            Status = UserSyncHealthStatus.TerminalConflict,
            ObservedHash = Enumerable.Repeat((byte)0x11, 32).ToArray(),
            ConflictingHash = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            DiagnosticCode = "irreconcilable-local-changes", BlocksPublishing = true, BlocksLogin = true
        };
        var first = await service.RecordAsync(descriptor);
        await database.UnitOfWork.SaveChangesAsync();
        for (var i = 0; i < 20; i++)
        {
            await service.RecordAsync(descriptor with
            {
                Status = UserSyncHealthStatus.AwaitingEvidence, BlocksPublishing = false, BlocksLogin = false,
                ObservedHash = [0x99], ConflictingHash = [], DiagnosticCode = "ordinary-corruption"
            });
            await service.MarkLocalCanonicalRecoveredAsync(userId);
            await database.UnitOfWork.SaveChangesAsync();
        }
        using var readback = database.OpenSession();
        var retained = (await new PasswordManagerLocal.Common.Backend.Repositories.UserSyncFaultRepository(readback)
            .ListForUserAsync(userId)).Single();
        MSTestAssert.AreEqual(first.Id, retained.Id);
        MSTestAssert.AreEqual(UserSyncHealthStatus.TerminalConflict, retained.Status);
        MSTestAssert.AreEqual(descriptor.DiagnosticCode, retained.DiagnosticCode);
        CollectionAssert.AreEqual(descriptor.ObservedHash, retained.ObservedHash);
        CollectionAssert.AreEqual(descriptor.ConflictingHash, retained.ConflictingHash);
        MSTestAssert.AreEqual(first.FirstDetectedAtUtc, retained.FirstDetectedAtUtc);
        MSTestAssert.AreEqual(first.LastDetectedAtUtc, retained.LastDetectedAtUtc);
        MSTestAssert.IsNull(retained.RecoveredAtUtc);
        MSTestAssert.IsTrue(retained.BlocksPublishing && retained.BlocksLogin);
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("Security")]
    public async Task OriginRecovery_RequiresHigherRevisionAndCannotClearLoginOrAuthorityFaults()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var service = new UserSyncFaultService(database.UserSyncFaults);
        var userId = (await CreateFaultUserAsync(database)).UId; var deviceId = Guid.NewGuid(); var originId = Guid.NewGuid();
        var descriptor = new UserSyncFaultDescriptor
        {
            UserId = userId, Scope = UserSyncFaultScope.SnapshotOrigin,
            Kind = UserSyncFaultKind.IncomingIntegrityFailure, Status = UserSyncHealthStatus.Isolated,
            OriginDeviceId = deviceId, OriginInstanceId = originId, KeyEpoch = 1, OriginRevision = 7,
            BlocksMerge = true
        };
        await service.RecordAsync(descriptor);
        await service.RecordAsync(descriptor with
        {
            Scope = UserSyncFaultScope.LoginIdentity, Kind = UserSyncFaultKind.LoginProjectionConflict,
            Status = UserSyncHealthStatus.TerminalConflict, BlocksLogin = true
        });
        await database.UnitOfWork.SaveChangesAsync();
        await service.MarkOriginRecoveredAsync(userId, deviceId, originId, 1, 7);
        await database.UnitOfWork.SaveChangesAsync();
        MSTestAssert.HasCount(2, await database.UserSyncFaults.ListActiveForUserAsync(userId));
        await service.MarkOriginRecoveredAsync(userId, deviceId, originId, 1, 8);
        await database.UnitOfWork.SaveChangesAsync();
        var remaining = (await database.UserSyncFaults.ListActiveForUserAsync(userId)).Single();
        MSTestAssert.AreEqual(UserSyncFaultKind.LoginProjectionConflict, remaining.Kind);
        MSTestAssert.AreEqual(UserSyncHealthStatus.TerminalConflict, remaining.Status);
        MSTestAssert.IsTrue(remaining.BlocksLogin);
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("Security")]
    public async Task DueRecovery_ExcludesAccountsWithTerminalEvidence_WithoutBlockingOtherProfiles()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var service = new UserSyncFaultService(database.UserSyncFaults);
        var blocked = (await CreateFaultUserAsync(database)).UId;
        var healthyAuthority = (await CreateFaultUserAsync(database)).UId;
        foreach (var userId in new[] { blocked, healthyAuthority })
            await service.RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = userId, Scope = UserSyncFaultScope.LocalCanonical,
                Kind = UserSyncFaultKind.CanonicalCheckpointMismatch, Status = UserSyncHealthStatus.AwaitingEvidence,
                BlocksPublishing = true
            });
        await service.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = blocked, Scope = UserSyncFaultScope.SnapshotFork, Kind = UserSyncFaultKind.SameRevisionFork,
            Status = UserSyncHealthStatus.TerminalConflict, OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(), KeyEpoch = 1, BlocksLogin = true
        });
        await database.UnitOfWork.SaveChangesAsync();
        using var readback = database.OpenSession();
        var repository = new PasswordManagerLocal.Common.Backend.Repositories.UserSyncFaultRepository(readback);
        for (var i = 0; i < 10; i++)
            MSTestAssert.AreEqual(healthyAuthority,
                (await repository.ListRecoverableLocalCanonicalAsync(DateTimeOffset.UtcNow.AddDays(i))).Single().UserId);
        MSTestAssert.HasCount(2, await repository.ListActiveForUserAsync(blocked));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DifferentFaultKinds_InSameScopeAndEpoch_RemainIndependent()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
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

    private static async Task<User> CreateFaultUserAsync(SqliteRepositoryTestDatabase database)
    {
        var user = new User { UId = Guid.NewGuid(), KeyEpoch = 1, MembershipEpoch = 1 };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        return user;
    }

}
