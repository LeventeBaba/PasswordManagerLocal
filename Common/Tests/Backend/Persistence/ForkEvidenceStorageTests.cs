using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
public sealed class ForkEvidenceStorageTests
{
    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("StorageBudget")]
    public async Task CompetingEnvelope_IsChargedWithinExistingCapacityAndSurvivesInitialization()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = new User { KeyEpoch = 1, MembershipEpoch = 1, EncryptedPayload = [0x11] };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        var snapshot = new UserSyncSnapshot
        {
            UserId = user.UId, OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(),
            OriginRevision = 1, UserKeyEpoch = 1, MembershipEpoch = 1, CreatedAtUtc = DateTimeOffset.UtcNow,
            EnvelopePayload = [0x11], SnapshotHash = [0x22], OriginSignPublicKey = [0x33], OriginSignature = [0x44]
        };
        await database.UserSyncSnapshots.AddAsync(snapshot);
        await database.UnitOfWork.SaveChangesAsync();
        await using var connection = database.Connections.CreateConnection();
        await connection.OpenAsync();
        foreach (var table in new[] { "UserSyncSnapshots", "UserRevisionKnowledge", "UserSyncFaults" })
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
            using var reader = await check.ExecuteReaderAsync();
            Assert.IsFalse(await reader.ReadAsync(), "Historical evidence must survive local profile removal.");
        }
        long originalUsage;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT UsedBytes FROM StorageBudgets WHERE Category = 'Snapshots';";
            originalUsage = (long)(await read.ExecuteScalarAsync())!;
        }
        using (var budget = connection.CreateCommand())
        {
            budget.CommandText = "UPDATE StorageBudgets SET LimitBytes = $limit WHERE Category = 'Snapshots';";
            budget.Parameters.AddWithValue("$limit", originalUsage + 512);
            await budget.ExecuteNonQueryAsync();
        }
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE UserSyncSnapshots SET ConflictingEnvelopePayload = $proof WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", snapshot.Id);
            var bytes = update.Parameters.AddWithValue("$proof", new byte[128]);
            await update.ExecuteNonQueryAsync();
            bytes.Value = new byte[1024];
            await Assert.ThrowsExactlyAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        }
        await AppDatabaseInitializer.InitializeAsync(connection);
        await AppDatabaseInitializer.InitializeAsync(connection);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT LimitBytes FROM StorageBudgets WHERE Category = 'Snapshots';";
            Assert.AreEqual(originalUsage + 512, (long)(await read.ExecuteScalarAsync())!);
            read.CommandText = "SELECT UsedBytes FROM StorageBudgets WHERE Category = 'Snapshots';";
            Assert.AreEqual(originalUsage + 128, (long)(await read.ExecuteScalarAsync())!);
        }
        var persisted = (await database.UserSyncSnapshots.ListForUserAsync(user.UId)).Single();
        Assert.IsNotNull(persisted.ConflictingEnvelopePayload);
        Assert.AreEqual(128, persisted.ConflictingEnvelopePayload.Length);
        CollectionAssert.AreEqual(user.EncryptedPayload, (await database.Users.GetByIdAsync(user.UId))!.EncryptedPayload);
    }
}
