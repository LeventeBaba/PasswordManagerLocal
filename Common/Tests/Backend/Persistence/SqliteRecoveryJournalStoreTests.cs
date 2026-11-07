using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteRecoveryJournalStoreTests
{
    [TestMethod]
    public async Task ReadbackUsesFlushedRowsAndReplacementRejectsAnOlderCiphertext()
    {
        await using var baseline = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var connections = new SqliteConnectionFactory(baseline.ConnectionString);
        using var session = new SqliteSession(connections);
        using var otherSession = new SqliteSession(connections);
        var journal = new SqliteUserRecoveryJournalStore(session);
        var otherJournal = new SqliteUserRecoveryJournalStore(otherSession);
        var row = new UserRecoveryJournal
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            EncryptedPayload = [1],
            ProtectedKey = [2],
            PayloadHash = [3]
        };

        Assert.IsFalse(journal.HasActiveTransaction);
        await using (var transaction = await session.BeginTransactionAsync())
        {
            Assert.IsTrue(journal.HasActiveTransaction);
            journal.Add(row);
            Assert.IsNull(await journal.ReadPersistedAsync(row.Id, default));
            await session.SaveChangesAsync();
            var persisted = await journal.ReadPersistedAsync(row.Id, default);
            Assert.IsNotNull(persisted);
            Assert.AreNotSame(row, persisted);
            CollectionAssert.AreEqual(row.EncryptedPayload, persisted.EncryptedPayload);
            await transaction.RollbackAsync();
        }
        Assert.IsFalse(journal.HasActiveTransaction);
        Assert.IsNull(await journal.ReadPersistedAsync(row.Id, default));

        await using (var transaction = await session.BeginTransactionAsync())
        {
            journal.Add(row);
            await session.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var stale = (await otherJournal.GetForWriteAsync(row.Id, default))!;
        await using (var transaction = await session.BeginTransactionAsync())
        {
            row.State = UserRecoveryJournalState.UnresolvedEvidence;
            row.EncryptedPayload = [4];
            journal.Update(row);
            Assert.AreEqual(UserRecoveryJournalState.PendingVerification, (await journal.ReadPersistedAsync(row.Id, default))!.State);
            await session.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        otherJournal.Delete(stale);
        var conflict = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => otherSession.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, conflict.Kind);
        var retained = (await new EfUserRecoveryJournalStore(baseline.Db).ReadPersistedAsync(row.Id, default))!;
        Assert.AreEqual(UserRecoveryJournalState.UnresolvedEvidence, retained.State);
        CollectionAssert.AreEqual(row.EncryptedPayload, retained.EncryptedPayload);
    }
}
