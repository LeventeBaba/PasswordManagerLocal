using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotPublisherServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetOrCreateAsync_AssignsDurableMonotonicRevisionsAndReusesUnchangedSnapshot()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var service = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            new UserLifecycleCoordinator());

        var first = await service.GetOrCreateAsync(user);
        var firstEnvelope = JsonSerializer.Deserialize(
            first.EnvelopePayload,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        var unchanged = await service.GetOrCreateAsync(user);
        var firstId = first.Id;
        var firstRevision = first.OriginRevision;
        var unchangedId = unchanged.Id;
        var unchangedRevision = unchanged.OriginRevision;

        user.EncryptedUserPasswordsDataPayload = [0x99, 0x01];
        user.UserPasswordsDataLastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        user.UserDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt;
        user.LastModifiedAt = user.UserPasswordsDataLastModifiedAt;
        user.GenerateIntegrityHash();
        database.Users.Update(user);
        var second = await service.GetOrCreateAsync(user);

        database.Db.ChangeTracker.Clear();
        var snapshots = await database.Db.UserSyncSnapshots
            .Where(snapshot => snapshot.UserId == user.UId)
            .ToListAsync();
        var state = await database.UserSyncStates.GetAsync(user.UId);
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId,
            identity.LocalDeviceId,
            identity.OriginInstanceId,
            user.KeyEpoch);

        MSTestAssert.AreEqual(1L, firstRevision);
        MSTestAssert.IsNotNull(firstEnvelope);
        var selfCoverage = firstEnvelope.Coverage.Single(entry =>
            entry.OriginDeviceId == identity.LocalDeviceId &&
            entry.OriginInstanceId == identity.OriginInstanceId &&
            entry.UserKeyEpoch == user.KeyEpoch);
        MSTestAssert.AreEqual(1L, selfCoverage.OriginRevision);
        MSTestAssert.AreEqual(firstId, unchangedId);
        MSTestAssert.AreEqual(1L, unchangedRevision);
        MSTestAssert.AreEqual(2L, second.OriginRevision);
        MSTestAssert.HasCount(1, snapshots);
        MSTestAssert.AreEqual(2L, snapshots[0].OriginRevision);
        MSTestAssert.IsNotNull(state);
        MSTestAssert.AreEqual(3L, state.NextOriginRevision);
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(2L, knowledge.HighestStoredRevision);
        MSTestAssert.AreEqual(2L, knowledge.HighestMergedRevision);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetOrCreateAsync_NewInstallationInstance_RestartsRevisionInSeparateOriginNamespace()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var service = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            new UserLifecycleCoordinator());

        var first = await service.GetOrCreateAsync(user);
        var oldInstance = identity.OriginInstanceId;
        identity.OriginInstanceId = Guid.NewGuid();
        var afterReset = await service.GetOrCreateAsync(user);

        var snapshots = await database.Db.UserSyncSnapshots
            .Where(snapshot => snapshot.UserId == user.UId)
            .OrderBy(snapshot => snapshot.OriginInstanceId)
            .ToListAsync();

        MSTestAssert.AreEqual(1L, first.OriginRevision);
        MSTestAssert.AreEqual(1L, afterReset.OriginRevision);
        MSTestAssert.AreNotEqual(oldInstance, afterReset.OriginInstanceId);
        MSTestAssert.HasCount(2, snapshots);
        MSTestAssert.AreEqual(2, snapshots.Select(snapshot => snapshot.OriginInstanceId).Distinct().Count());
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetOrCreateAsync_UnhealthyCanonicalGateDoesNotCreateOrSignSnapshot()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var service = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            canonicalHealth: new FixedCanonicalHealthService(new CanonicalHealthResult(
                UserDataVerificationState.CheckpointFailure,
                UserDataBlobKind.All,
                UserSyncKeyConfidence.UnconfirmedPassword,
                "canonical-checkpoint-mismatch")
            {
                RowIntegrityVerified = true,
                CheckpointVerified = false
            }));

        await MSTestAssert.ThrowsAsync<InvalidDataException>(() => service.GetOrCreateAsync(user));

        MSTestAssert.AreEqual(0, await database.Db.UserSyncSnapshots.CountAsync());
        MSTestAssert.AreEqual(0, await database.Db.UserSyncStates.CountAsync());
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task GetOrCreateAsync_HealthFailureRetainsPriorPublicationAsRecoveryEvidenceAndSchedulesRetry()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var lifecycle = new UserLifecycleCoordinator();
        var initialPublisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            lifecycle);
        var published = await initialPublisher.GetOrCreateAsync(user);
        var scheduler = new FakeUserDataRecoveryScheduler();
        var blockedPublisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            lifecycle,
            canonicalHealth: new FixedCanonicalHealthService(new CanonicalHealthResult(
                UserDataVerificationState.RootIntegrityFailure,
                UserDataBlobKind.All,
                UserSyncKeyConfidence.AuthenticatedSession,
                "canonical-root-integrity-failed")),
            recoveryScheduler: scheduler);

        await MSTestAssert.ThrowsAsync<InvalidDataException>(() => blockedPublisher.GetOrCreateAsync(user));

        database.Db.ChangeTracker.Clear();
        var retained = await database.UserSyncSnapshots.GetAsync(
            user.UId,
            identity.LocalDeviceId,
            identity.OriginInstanceId,
            user.KeyEpoch);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(published.Id, retained.Id);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, retained.Status);
        MSTestAssert.AreEqual(
            (user.UId, UserDataRecoveryTrigger.PublisherHealthFailure),
            scheduler.Calls.Single());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetOrCreateAsync_AfterHealthRecovery_ReusesIsolatedLocalOriginRowWithNewRevision()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var lifecycle = new UserLifecycleCoordinator();
        var initialPublisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            lifecycle);

        var first = await initialPublisher.GetOrCreateAsync(user);
        first.Status = UserSyncSnapshotStatus.IsolatedCorrupt;
        first.QuarantineReason = "canonical-checkpoint-mismatch";
        database.UserSyncSnapshots.Update(first);
        await database.UnitOfWork.SaveChangesAsync();

        var recoveredPublisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            lifecycle,
            canonicalHealth: new FixedCanonicalHealthService(new CanonicalHealthResult(
                UserDataVerificationState.Healthy,
                UserDataBlobKind.None,
                UserSyncKeyConfidence.ExplicitlyTrusted,
                "canonical-fully-verified")
            {
                RowIntegrityVerified = true,
                CheckpointVerified = true,
                FullyVerified = true
            }));

        var republished = await recoveredPublisher.GetOrCreateAsync(user);
        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserSyncSnapshots.Where(row => row.UserId == user.UId).ToListAsync();

        MSTestAssert.HasCount(1, rows);
        MSTestAssert.AreEqual(first.Id, republished.Id);
        MSTestAssert.AreEqual(2L, republished.OriginRevision);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.LocalPublished, republished.Status);
        MSTestAssert.IsNull(republished.QuarantineReason);
    }

    private static User CreateUser()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            PasswordSalt = [0x03],
            EncryptedPayload = [0x10],
            EncryptedGeneralUserDataPayload = [0x20],
            EncryptedUserPasswordsDataPayload = [0x30],
            EncryptedUserDevicesDataPayload = [0x40],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1_000,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = Guid.Parse("B7566B3E-EC72-42A3-A3E5-64F5CFF413CF"),
            GeneralDataVersionOriginInstanceId = Guid.Parse("D959DA77-A434-481E-A5CE-EAB1A5F01701"),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        return user;
    }

    private static FakeDeviceIdentityService CreateIdentity(Key key) =>
        new()
        {
            LocalDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(key, data)
        };

}
