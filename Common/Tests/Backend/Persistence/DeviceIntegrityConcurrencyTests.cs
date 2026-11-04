using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
public sealed class DeviceIntegrityConcurrencyTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StaleDeviceObservation_CannotInvalidateConcurrentMetadataCommit()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var initialTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seeded = new Device
        {
            PublicKey = [0x12],
            SignPublicKey = [0x34],
            TlsCertFingerprint = "AABBCC",
            IsTrusted = true,
            LastSeen = initialTime,
            LastSync = initialTime,
            LastModifiedAt = new DateTimeOffset(initialTime)
        };
        database.Db.Devices.Add(seeded);
        await database.Db.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(database.Db.Database.GetDbConnection()).Options;
        await using var observationDb = new AppDbContext(options);
        var observation = await observationDb.Devices.SingleAsync(device => device.Id == seeded.Id);

        // Simulate another delta changing public metadata after the observation scope read the row.
        seeded.LastModifiedAt = seeded.LastModifiedAt.AddSeconds(1);
        await database.Db.SaveChangesAsync();

        var observedAt = initialTime.AddSeconds(2);
        observation.LastSeen = observedAt;
        observation.LastSync = observedAt;
        await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(async () =>
        {
            await observationDb.SaveChangesAsync();
        });

        await using var freshDb = new AppDbContext(options);
        var fresh = await freshDb.Devices.SingleAsync(device => device.Id == seeded.Id);
        Assert.IsTrue(fresh.IsIntegrityValid());
        Assert.AreEqual(seeded.LastModifiedAt, fresh.LastModifiedAt);
        Assert.AreEqual(initialTime, fresh.LastSeen);

        fresh.LastSeen = observedAt;
        fresh.LastSync = observedAt;
        await freshDb.SaveChangesAsync();

        var persisted = await freshDb.Devices.AsNoTracking().SingleAsync(device => device.Id == seeded.Id);
        Assert.IsTrue(persisted.IsIntegrityValid(), "Endpoint hints require a valid device integrity hash.");
        Assert.AreEqual(seeded.LastModifiedAt, persisted.LastModifiedAt);
        Assert.AreEqual(observedAt, persisted.LastSeen);
        Assert.AreEqual(observedAt, persisted.LastSync);
    }
}
