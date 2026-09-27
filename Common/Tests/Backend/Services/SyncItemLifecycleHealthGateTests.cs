using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncItemLifecycleHealthGateTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TouchLocalStateAsync_UnhealthyCanonicalBaseline_DoesNotResignOrMutateUser()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var originalModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var user = new User
        {
            UId = Guid.NewGuid(),
            KeyEpoch = 1,
            MembershipEpoch = 1,
            LastModifiedAt = originalModifiedAt
        };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var health = new RejectingCanonicalHealthService();
        var lifecycle = new SyncItemLifecycleService(
            database.SyncItems,
            database.SyncQueue,
            database.Users,
            database.Groups,
            database.Devices,
            database.UserDevices,
            database.Tombstones,
            localDevices: null!,
            canonicalHealth: health);

        await MSTestAssert.ThrowsExactlyAsync<InvalidDataException>(() => lifecycle.TouchLocalStateAsync(
            new SyncItem
            {
                ModelId = user.UId,
                ModelType = SyncModelType.User,
                ChangeType = SyncChangeType.Updated
            },
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        database.Db.ChangeTracker.Clear();
        var reloaded = await database.Users.GetByIdAsync(user.UId);
        MSTestAssert.IsNotNull(reloaded);
        MSTestAssert.AreEqual(originalModifiedAt, reloaded.LastModifiedAt);
        MSTestAssert.AreEqual(1, health.VerifyCalls);
        MSTestAssert.AreEqual(0, health.UpdateCheckpointCalls);
    }

}
