using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
public sealed class SqliteVaultSchemaTests
{
    [TestMethod]
    public async Task FreshSchemaMatchesExistingEfSchema()
    {
        await using var legacy = await SqliteIntegrationTestDatabase.CreateAsync();
        await using var current = new SqliteConnection("Data Source=:memory:");
        await current.OpenAsync();
        await AppDatabaseInitializer.InitializeAsync(current);
        var legacyConnection = legacy.Db.Database.GetDbConnection();

        await AssertSchemaMatchesAsync(legacyConnection, current, "columns and primary keys", """
            SELECT printf('%s|%s|%s|%d|%s|%d', t.name, c.name, c.type, c."notnull", c.dflt_value, c.pk)
            FROM sqlite_schema AS t JOIN pragma_table_info(t.name) AS c
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*'
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(legacyConnection, current, "foreign keys", """
            SELECT printf('%s|%s|%s|%s|%s|%s', t.name, f."from", f."table", f."to", f.on_update, f.on_delete)
            FROM sqlite_schema AS t JOIN pragma_foreign_key_list(t.name) AS f
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*'
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(legacyConnection, current, "indexes and unique constraints", """
            SELECT printf('%s|%s|%s|%d|%d|%d|%s|%s|%d', t.name,
                          CASE WHEN i.origin = 'c' THEN i.name ELSE '' END,
                          i.origin, i."unique", i.partial, c.seqno, c.name, c.coll, c."desc")
            FROM sqlite_schema AS t
            JOIN pragma_index_list(t.name) AS i
            JOIN pragma_index_xinfo(i.name) AS c
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*' AND c."key" = 1
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(legacyConnection, current, "partial index predicates", """
            SELECT name || '|' || trim(substr(sql, instr(sql, ' WHERE ') + 7))
            FROM sqlite_schema
            WHERE type = 'index' AND instr(sql, ' WHERE ') > 0
            ORDER BY 1;
            """);
        await AssertSchemaMatchesAsync(legacyConnection, current, "autoincrement and check constraints", """
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
        DbConnection legacy,
        DbConnection current,
        string aspect,
        string sql)
    {
        CollectionAssert.AreEqual(await ReadSchemaAsync(legacy, sql), await ReadSchemaAsync(current, sql), aspect);
    }

    private static async Task<string[]> ReadSchemaAsync(DbConnection connection, string sql)
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
