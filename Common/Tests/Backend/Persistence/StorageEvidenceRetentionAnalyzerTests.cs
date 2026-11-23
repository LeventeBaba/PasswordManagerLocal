using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("EvidenceCompaction")]
public sealed class StorageEvidenceRetentionAnalyzerTests
{
    [TestMethod]
    public async Task EveryEvidenceCategoryHasImmutableIdentityAndDependencies_WithoutReadingSecretsOrWritingState()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        var id = Guid.NewGuid();
        foreach (var table in SqliteStorageRetentionAnalyzer.EvidenceTables)
            await InsertRowAsync(connection, table, id);
        var before = await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageUsage;");
        using var session = new SqliteSession(vault.Connections);
        var analyzer = new SqliteStorageRetentionAnalyzer(session);
        foreach (var table in SqliteStorageRetentionAnalyzer.EvidenceTables)
        {
            var page = await analyzer.AnalyzePageAsync(table, limit: 1);
            Assert.AreEqual(1, page.Records.Count, table);
            Assert.IsFalse(string.IsNullOrEmpty(page.Records[0].ImmutableIdentity), table);
            Assert.IsTrue(page.Records[0].Dependencies.Count > 0 || table == "RevocationEvidenceDebts", table);
            Assert.AreEqual(0L, page.Records[0].ReclaimableBytes, table);
            Assert.IsNull(page.Next, table);
        }
        Assert.AreEqual(before, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageUsage;"));
        Assert.IsFalse(session.HasPendingChanges);
    }

    [TestMethod]
    public async Task CompositeReceiptCursorPreservesExactKindAndHashNamespaces_AndRejectsCrossScopeReuse()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        // Match repository Guid parameter binding for the user-scoped lookup.
        await InsertRowAsync(connection, "RevocationDeliveryReceipts", id,
            new Dictionary<string, object> { ["UserId"] = id });
        await InsertRowAsync(connection, "RevocationDeliveryReceipts", id,
            new Dictionary<string, object> { ["UserId"] = id, ["Kind"] = 2L });
        foreach (var marker in new byte[] { 1, 2 })
        {
            var attestedHash = new byte[32]; attestedHash[0] = marker;
            await InsertRowAsync(connection, "RevocationDeliveryReceipts", id,
                new Dictionary<string, object> { ["UserId"] = id, ["Kind"] = 3L, ["AttestedReceiptHash"] = attestedHash });
        }
        using var session = new SqliteSession(vault.Connections);
        var analyzer = new SqliteStorageRetentionAnalyzer(session);
        var first = await analyzer.AnalyzePageAsync("RevocationDeliveryReceipts", id, limit: 1);
        Assert.AreEqual(1, first.Records.Count);
        Assert.AreEqual(id, first.Records.Single().UserId);
        Assert.IsNotNull(first.Next);
        var second = await analyzer.AnalyzePageAsync("RevocationDeliveryReceipts", id, first.Next, limit: 1);
        Assert.IsNotNull(second.Next);
        Assert.AreNotEqual(first.Records.Single().ImmutableIdentity, second.Records.Single().ImmutableIdentity);
        Assert.AreEqual(StorageRetentionReason.RequiredAsExactSignedReceipt, second.Records.Single().Reason);
        var third = await analyzer.AnalyzePageAsync("RevocationDeliveryReceipts", id, second.Next, limit: 1);
        Assert.IsNotNull(third.Next);
        var fourth = await analyzer.AnalyzePageAsync("RevocationDeliveryReceipts", id, third.Next, limit: 1);
        Assert.IsNull(fourth.Next);
        Assert.AreNotEqual(third.Records.Single().ImmutableIdentity, fourth.Records.Single().ImmutableIdentity,
            "Custody receipts with different attested hashes must remain separate cursor keys.");
        var identities = new[] { first, second, third, fourth }.Select(page => page.Records.Single().ImmutableIdentity).ToArray();
        Assert.AreEqual(4, identities.Distinct(StringComparer.Ordinal).Count());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => analyzer.AnalyzePageAsync("RevocationDeliveryReceipts", Guid.NewGuid(), first.Next));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => analyzer.AnalyzePageAsync("UserSyncFaults", id, first.Next));
    }
}
