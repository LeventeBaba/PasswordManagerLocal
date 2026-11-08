using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncTombstoneRepositoryTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task UpsertAsync_WhenSafetyLimitReached_ThrowsWithoutDiscardingOlderTombstones()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var startTs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

        for (var i = 0; i < TombstoneConstants.MaxSyncTombstones; i++)
            await database.Tombstones.UpsertAsync(Guid.NewGuid(), SyncModelType.Group, startTs + i);

        await database.UnitOfWork.SaveChangesAsync();

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await database.Tombstones.UpsertAsync(Guid.NewGuid(), SyncModelType.Group, startTs + TombstoneConstants.MaxSyncTombstones));

        var timestamps = await database.UnitOfWork.ReadAsync("""
            SELECT "DeletedAtTs" FROM "SyncTombstones" ORDER BY "DeletedAtTs";
            """, row => row.GetInt64(0), default);

        MSTestAssert.HasCount(TombstoneConstants.MaxSyncTombstones, timestamps);
        MSTestAssert.AreEqual(startTs, timestamps[0]);
        MSTestAssert.AreEqual(startTs + TombstoneConstants.MaxSyncTombstones - 1, timestamps[^1]);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task UpsertAsync_SameTombstoneBeforeSave_CoalescesPendingWrite()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var modelId = Guid.NewGuid();
        var olderTs = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        var newerTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await database.Tombstones.UpsertAsync(modelId, SyncModelType.User, olderTs);
        await database.Tombstones.UpsertAsync(modelId, SyncModelType.User, newerTs);
        MSTestAssert.AreEqual(1, await database.UnitOfWork.SaveChangesAsync());

        var tombstone = (await database.Tombstones.GetAsync(modelId, SyncModelType.User))!;
        MSTestAssert.AreEqual(modelId, tombstone.ModelId);
        MSTestAssert.AreEqual(SyncModelType.User, tombstone.ModelType);
        MSTestAssert.AreEqual(newerTs, tombstone.DeletedAtTs);
    }

}
