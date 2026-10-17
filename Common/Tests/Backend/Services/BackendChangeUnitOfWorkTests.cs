using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class BackendChangeUnitOfWorkTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task CommittedMutation_PublishesCombinedUserScopesOnlyAfterCommit()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var unitOfWork = new AppUnitOfWork(database.Db, hub);
        var user = await AddUserAsync(database, unitOfWork);
        await using var subscription = hub.Subscribe(user.UId, capacity: 8);

        user.EncryptedUserPasswordsDataPayload = [0x51, 0x52];
        user.EncryptedGeneralUserDataPayload = [0x61, 0x62];
        user.GenerateIntegrityHash();
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SaveChangesAsync();

        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);
        await transaction.CommitAsync();

        var delivery = (await ReadExactlyAsync(subscription, 1)).Single();
        Assert.AreEqual(2L, delivery.Sequence);
        Assert.AreEqual(
            FrontendChangeScope.UserProfile | FrontendChangeScope.Passwords,
            delivery.Notification!.Scope);
        Assert.AreEqual(user.UId, delivery.Notification.UserId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task RolledBackMutation_DoesNotPublishAndIrrelevantTimestampUpdateIsNoOp()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var unitOfWork = new AppUnitOfWork(database.Db, hub);
        var user = await AddUserAsync(database, unitOfWork);
        var committedSequence = hub.CurrentPosition.Sequence;

        user.LastModifiedAt = user.LastModifiedAt.AddSeconds(1);
        user.GenerateIntegrityHash();
        await unitOfWork.SaveChangesAsync();
        Assert.AreEqual(committedSequence, hub.CurrentPosition.Sequence);

        user.EncryptedUserPasswordsDataPayload = [0x71, 0x72];
        user.GenerateIntegrityHash();
        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            await unitOfWork.SaveChangesAsync();
            Assert.AreEqual(committedSequence, hub.CurrentPosition.Sequence);
            await transaction.RollbackAsync();
        }

        Assert.AreEqual(committedSequence, hub.CurrentPosition.Sequence);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DurablePartialCommit_IsPublishedEvenWhenLaterWorkFails()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var unitOfWork = new AppUnitOfWork(database.Db, hub);
        var user = await AddUserAsync(database, unitOfWork);
        await using var subscription = hub.Subscribe(user.UId, capacity: 8);
        var startSequence = hub.CurrentPosition.Sequence;

        user.EncryptedGeneralUserDataPayload = [0x73, 0x74];
        user.GenerateIntegrityHash();
        await unitOfWork.SaveChangesAsync();
        try
        {
            throw new InvalidOperationException("Simulated follow-up failure after a durable save.");
        }
        catch (InvalidOperationException)
        {
            // The persisted profile change remains authoritative and must still be announced.
        }

        var delivery = (await ReadExactlyAsync(subscription, 1)).Single();
        Assert.AreEqual(startSequence + 1, delivery.Sequence);
        Assert.AreEqual(FrontendChangeScope.UserProfile, delivery.Notification!.Scope);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EffectiveLoginProjectionChangesPublishButVersionOnlyUpdatesDoNot()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var unitOfWork = new AppUnitOfWork(database.Db, hub);
        var user = await AddUserAsync(database, unitOfWork);
        var identity = new UserLoginIdentityState
        {
            UserId = user.UId,
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            SourceSnapshotHash = Enumerable.Repeat((byte)0x03, 32).ToArray(),
            Status = UserLoginIdentityStatus.Active,
            ConcurrencyVersion = 1
        };
        await database.Db.UserLoginIdentityStates.AddAsync(identity);
        await unitOfWork.SaveChangesAsync();
        var sequenceAfterProjection = hub.CurrentPosition.Sequence;

        identity.VersionLogicalCounter++;
        identity.ConcurrencyVersion++;
        await unitOfWork.SaveChangesAsync();
        Assert.AreEqual(sequenceAfterProjection, hub.CurrentPosition.Sequence);

        identity.UsernameHash = Enumerable.Repeat((byte)0x04, 32).ToArray();
        identity.ConcurrencyVersion++;
        await unitOfWork.SaveChangesAsync();

        Assert.AreEqual(sequenceAfterProjection + 1, hub.CurrentPosition.Sequence);
    }

    private static async Task<User> AddUserAsync(
        SqliteIntegrationTestDatabase database,
        AppUnitOfWork unitOfWork)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x10, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x20, 32).ToArray(),
            PasswordSalt = [0x30, 0x31],
            EncryptedPayload = [0x40],
            EncryptedGeneralUserDataPayload = [0x41],
            EncryptedUserPasswordsDataPayload = [0x42],
            EncryptedUserDevicesDataPayload = [0x43],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = Guid.NewGuid(),
            GeneralDataVersionOriginInstanceId = Guid.NewGuid(),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await unitOfWork.SaveChangesAsync();
        return user;
    }

    private static async Task<IReadOnlyList<BackendChangeDelivery>> ReadExactlyAsync(
        IBackendChangeSubscription subscription,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deliveries = new List<BackendChangeDelivery>(count);
        await foreach (var delivery in subscription.ReadAllAsync(timeout.Token))
        {
            deliveries.Add(delivery);
            if (deliveries.Count == count)
                return deliveries;
        }

        return deliveries;
    }
}
