using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
public sealed class SqliteVaultSchemaTests
{
    [TestMethod]
    public async Task FreshSchemaMatchesFrozenVaultSchema()
    {
        SQLitePCL.Batteries_V2.Init();
        var baseline = await ReadBaselineAsync();
        await using var current = new SqliteConnection("Data Source=:memory:");
        await current.OpenAsync();
        await AppDatabaseInitializer.InitializeAsync(current);

        await AssertSchemaMatchesAsync(baseline, current, "columns and primary keys", """
            SELECT printf('%s|%s|%s|%d|%s|%d', t.name, c.name, c.type, c."notnull", c.dflt_value, c.pk)
            FROM sqlite_schema AS t JOIN pragma_table_info(t.name) AS c
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*'
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(baseline, current, "foreign keys", """
            SELECT printf('%s|%s|%s|%s|%s|%s', t.name, f."from", f."table", f."to", f.on_update, f.on_delete)
            FROM sqlite_schema AS t JOIN pragma_foreign_key_list(t.name) AS f
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*'
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(baseline, current, "indexes and unique constraints", """
            SELECT printf('%s|%s|%s|%d|%d|%d|%s|%s|%d', t.name,
                          CASE WHEN i.origin = 'c' THEN i.name ELSE '' END,
                          i.origin, i."unique", i.partial, c.seqno, c.name, c.coll, c."desc")
            FROM sqlite_schema AS t
            JOIN pragma_index_list(t.name) AS i
            JOIN pragma_index_xinfo(i.name) AS c
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*' AND c."key" = 1
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(baseline, current, "partial index predicates", """
            SELECT name || '|' || trim(substr(sql, instr(sql, ' WHERE ') + 7))
            FROM sqlite_schema
            WHERE type = 'index' AND instr(sql, ' WHERE ') > 0
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(baseline, current, "autoincrement and check constraints", """
            SELECT name || '|AUTOINCREMENT'
            FROM sqlite_schema
            WHERE type = 'table' AND instr(sql, 'AUTOINCREMENT') > 0
            UNION ALL
            SELECT name || '|' || substr(sql, instr(sql, 'CHECK ('),
                   instr(substr(sql, instr(sql, 'CHECK (')), ')'))
            FROM sqlite_schema
            WHERE type = 'table' AND instr(sql, 'CHECK (') > 0
            ORDER BY 1;
            """);
    }

    private static async Task AssertSchemaMatchesAsync(
        string[] baseline,
        SqliteConnection current,
        string aspect,
        string sql)
    {
        var prefix = aspect + "|";
        var expected = baseline.Where(row => row.StartsWith(prefix, StringComparison.Ordinal))
            .Select(row => row[prefix.Length..]).ToArray();
        Assert.IsTrue(expected.Length > 0, $"The frozen schema has no entries for {aspect}.");
        CollectionAssert.AreEqual(expected, await ReadSchemaAsync(current, sql), aspect);
    }

    private static async Task<string[]> ReadBaselineAsync()
    {
        using var stream = typeof(SqliteVaultSchemaTests).Assembly
            .GetManifestResourceStream("PasswordManagerLocal.Common.Tests.VaultSchemaBaseline.txt")
            ?? throw new InvalidOperationException("The frozen vault schema resource is missing.");
        using var reader = new StreamReader(stream);
        var rows = new List<string>();
        while (await reader.ReadLineAsync() is { } row)
            rows.Add(row);
        return rows.ToArray();
    }

    private static async Task<string[]> ReadSchemaAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
}
