using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Persistence;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
public sealed class DatabaseEncryptionMigratorTests
{
    private const string TestPassword = "bWlncmF0aW9uLXRlc3QtZGF0YWJhc2Uta2V5";

    static DatabaseEncryptionMigratorTests() => Batteries_V2.Init();

    [TestMethod]
    public async Task MismatchedDatabaseKey_OffersUnreadableRecoveryWithoutErasingTheDatabase()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var connectionString = CreateUnpooledRuntimeConnectionString(paths.DatabasePath);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE RetainedData (Value TEXT); INSERT INTO RetainedData VALUES ('retained');";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(
            () => DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, "a-different-database-key"));

        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationTempPath));
        await using var preserved = new SqliteConnection(connectionString);
        await preserved.OpenAsync();
        await using var read = preserved.CreateCommand();
        read.CommandText = "SELECT Value FROM RetainedData;";
        Assert.AreEqual("retained", await read.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task ExistingSqlCipherDatabase_IsMigratedToSqlite3McAndPreservesData()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateLegacySqlCipherDatabaseAsync(paths.DatabasePath);

        var migrated = await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(
            paths,
            TestPassword,
            CancellationToken.None);

        Assert.IsTrue(migrated, "A SQLCipher database should be migrated on first backend initialization.");
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationTempPath));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationBackupPath));

        await using (var migratedConnection = new SqliteConnection(
                         CreateUnpooledRuntimeConnectionString(paths.DatabasePath)))
        {
            await migratedConnection.OpenAsync();
            await using var command = migratedConnection.CreateCommand();
            command.CommandText = "SELECT Id, Value FROM MigrationItems ORDER BY Id;";
            await using var reader = await command.ExecuteReaderAsync();

            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            Assert.AreEqual("first row", reader.GetString(1));
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(2, reader.GetInt32(0));
            Assert.AreEqual("second row", reader.GetString(1));
            Assert.IsFalse(await reader.ReadAsync());
        }

        await ExpectThrowsAsync<SqliteException>(async () =>
        {
            await using var legacyConnection = new SqliteConnection(
                CreateLegacySqlCipherConnectionString(paths.DatabasePath));
            await legacyConnection.OpenAsync();
            await using var command = legacyConnection.CreateCommand();
            command.CommandText = "SELECT Value FROM MigrationItems;";
            await command.ExecuteScalarAsync();
        });

        var secondMigration = await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(
            paths,
            TestPassword,
            CancellationToken.None);
        Assert.IsFalse(secondMigration, "A migrated database should be recognized without re-keying again.");
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }

    private static async Task CreateLegacySqlCipherDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            CreateLegacySqlCipherConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE MigrationItems (
                Id INTEGER PRIMARY KEY,
                Value TEXT NOT NULL
            );
            INSERT INTO MigrationItems (Id, Value)
            VALUES (1, 'first row'), (2, 'second row');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateUnpooledRuntimeConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder(
            DatabaseEncryptionMigrator.CreateRuntimeConnectionString(databasePath, TestPassword))
        {
            // Disposing a fixture connection must release the file immediately;
            // production runtime connections deliberately retain pooling.
            Pooling = false
        }.ToString();

    private static string CreateLegacySqlCipherConnectionString(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = $"{new Uri(Path.GetFullPath(databasePath)).AbsoluteUri}?cipher=sqlcipher&legacy=4",
            Password = TestPassword,
            Mode = mode,
            Pooling = false
        }.ToString();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PasswordManagerLocal.DatabaseEncryptionMigrationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
