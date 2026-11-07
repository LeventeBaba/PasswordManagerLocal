using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteDeviceRepositoryParityTests
{
    [TestMethod]
    public async Task LocalIdentity_DirectWritesAndEfReadbackPreserveKeysAndIntegrity()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        var repository = new SqliteDeviceIdentityRepository(session);
        Assert.IsNull(await repository.Get());

        var identity = CreateIdentity();
        await repository.Create(identity);
        Assert.IsNull(await repository.Get());
        Assert.AreEqual(1, await session.SaveChangesAsync());

        await using (var transaction = await session.BeginTransactionAsync())
        {
            identity.IsSyncOn = true;
            identity.GenerateIntegrityHash();
            repository.Update(identity);
            await session.SaveChangesAsync();
            Assert.IsTrue((await repository.Get())!.IsSyncOn);
            await transaction.RollbackAsync();
        }

        Assert.IsFalse((await repository.Get())!.IsSyncOn);
        identity.GenerateIntegrityHash();
        repository.Update(identity);
        await session.SaveChangesAsync();

        var readByEf = await database.Db.LocalDeviceIdentities.AsNoTracking().SingleAsync();
        Assert.AreEqual(identity.Id, readByEf.Id);
        Assert.AreEqual(identity.OriginInstanceId, readByEf.OriginInstanceId);
        Assert.AreEqual(identity.CreatedAt, readByEf.CreatedAt);
        CollectionAssert.AreEqual(identity.AgreementPrivateKeyBlob, readByEf.AgreementPrivateKeyBlob);
        Assert.IsTrue(readByEf.IsSyncOn);
        Assert.IsTrue(readByEf.IsIntegrityValid());
    }

    [TestMethod]
    public async Task DeviceUpdate_UsesOriginalTokenAndSupportsRepeatedFlushes()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var baseline = CreateDevice("AABBCC", [1]);
        await database.Devices.AddAsync(baseline);
        await database.UnitOfWork.SaveChangesAsync();

        var factory = new SqliteConnectionFactory(database.ConnectionString);
        using var firstSession = new SqliteSession(factory);
        using var secondSession = new SqliteSession(factory);
        var first = new SqliteDeviceRepository(firstSession);
        var second = new SqliteDeviceRepository(secondSession);
        var left = (await first.GetByIdAsync(baseline.Id))!;
        var right = (await second.GetByIdAsync(baseline.Id))!;

        await using (var transaction = await firstSession.BeginTransactionAsync())
        {
            left.LastModifiedAt = left.LastModifiedAt.AddSeconds(1);
            Array.Clear(left.IntegrityHash);
            first.Update(left);
            Assert.AreEqual(1, await firstSession.SaveChangesAsync());

            left.LastSeen = left.LastSeen.AddSeconds(2);
            first.Update(left);
            Assert.AreEqual(1, await firstSession.SaveChangesAsync());
            await transaction.CommitAsync();
        }

        right.IsBlocked = true;
        second.Update(right);
        var stale = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => secondSession.SaveChangesAsync());
        Assert.IsTrue(DeviceWriteConflict.IsStaleDevice(stale));
        Assert.IsFalse(DeviceWriteConflict.IsStaleDevice(
            new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(UserDevice))));

        var readByEf = (await database.Devices.GetByIdAsNoTrackingAsync(baseline.Id))!;
        Assert.AreEqual(left.LastModifiedAt, readByEf.LastModifiedAt);
        Assert.AreEqual(left.LastSeen, readByEf.LastSeen);
        Assert.IsFalse(readByEf.IsBlocked);
        Assert.IsTrue(readByEf.IsIntegrityValid());
    }

    [TestMethod]
    public async Task DeviceLookup_RequiresFullSigningKeyAndNormalizesFingerprint()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var match = CreateDevice("AABBCC", [7]);
        var collision = CreateDevice("DDEEFF", [9]);
        await database.Devices.AddAsync(match);
        await database.Devices.AddAsync(collision);
        await database.UnitOfWork.SaveChangesAsync();

        var expectedHash = Hashing.SHA256Hash(match.SignPublicKey);
        await database.Db.Devices.Where(row => row.Id == collision.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SignPublicKeyHash, expectedHash));

        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        var devices = new SqliteDeviceRepository(session);
        Assert.HasCount(2, await devices.ListBySigningKeyHashAsync(expectedHash));
        Assert.AreEqual(match.Id, (await devices.GetBySignPublicKeyAsync(match.SignPublicKey))!.Id);
        Assert.AreEqual(match.Id, (await devices.GetByTlsCertFingerprintAsync(" aa:bb:cc "))!.Id);
        Assert.IsNull(await devices.GetByTlsCertFingerprintAsync(" "));
        CollectionAssert.AreEquivalent(new[] { match.Id },
            (await devices.ListLocalSelfDevicesAsync(Guid.NewGuid(), match.SignPublicKey, "")).Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public async Task LinksAndRoutes_MatchEfScopeVerifyIntegrityAndCascade()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var user = new User();
        var disabled = new User();
        user.GenerateIntegrityHash();
        disabled.GenerateIntegrityHash();
        database.Db.Users.AddRange(user, disabled);
        await database.UnitOfWork.SaveChangesAsync();

        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        var identity = CreateIdentity();
        var device = CreateDevice("010203", [5]);
        var devices = new SqliteDeviceRepository(session);
        var remoteLinks = new SqliteUserDeviceRepository(session);
        var localLinks = new SqliteLocalUserDeviceRepository(session);
        var routes = new SqliteSyncRouteRepository(session);
        await new SqliteDeviceIdentityRepository(session).Create(identity);
        await devices.AddAsync(device);
        var link = new UserDevice { UserId = user.UId, DeviceId = device.Id };
        await remoteLinks.AddAsync(link);
        await remoteLinks.AddAsync(new UserDevice { UserId = disabled.UId, DeviceId = device.Id });
        await localLinks.AddAsync(new LocalUserDevice { UserId = user.UId, LocalDeviceIdentityId = identity.Id });
        await localLinks.AddAsync(new LocalUserDevice { UserId = disabled.UId, LocalDeviceIdentityId = identity.Id, IsSyncOn = false });
        Assert.AreEqual(6, await session.SaveChangesAsync());

        var joined = (await devices.GetByIdWithUserDevicesAsync(device.Id))!;
        Assert.HasCount(2, joined.UserDevices);
        Assert.AreEqual(identity.Id, (await localLinks.GetAsync(user.UId))!.LocalDeviceIdentity!.Id);
        Assert.AreEqual(link.ModelId, (await remoteLinks.GetByModelIdAsync(link.ModelId))!.ModelId);
        Assert.IsTrue(await routes.IsEligibleAsync(user.UId, device.Id));
        Assert.IsFalse(await routes.IsEligibleAsync(disabled.UId, device.Id));
        var requested = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid())
            .Concat([disabled.UId, user.UId, user.UId, Guid.Empty]).ToArray();
        CollectionAssert.AreEquivalent(
            (await database.SyncRoutes.ListEligibleUserIdsAsync(requested, device.Id)).ToArray(),
            (await routes.ListEligibleUserIdsAsync(requested, device.Id)).ToArray());

        await database.Db.UserDevices.Where(row => row.UserId == user.UId && row.DeviceId == device.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsSyncOn, false));
        await Assert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() => remoteLinks.GetWithDeviceAsync(user.UId, device.Id));
        await Assert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() => devices.GetByIdWithUserDevicesAsync(device.Id));

        devices.Delete(joined);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        Assert.IsFalse(await remoteLinks.ExistsAsync(user.UId, device.Id));
        Assert.IsTrue(await localLinks.IsSyncOnAsync(user.UId));
    }

    private static LocalDeviceIdentity CreateIdentity()
    {
        var row = new LocalDeviceIdentity
        {
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(11),
            AgreementPrivateKeyBlob = [1, 2],
            SignPrivateKeyBlob = [3, 4],
            PFXCertificate = [5, 6],
            DeviceType = DeviceType.WindowsPc
        };
        row.GenerateIntegrityHash();
        return row;
    }

    private static Device CreateDevice(string fingerprint, byte[] signingKey) => new()
    {
        TlsCertFingerprint = fingerprint,
        DeviceType = DeviceType.WindowsPc,
        PublicKey = [1],
        SignPublicKey = signingKey,
        IsTrusted = true
    };
}
