using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Errors;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

public sealed partial class DatabaseEncryptionMigratorTests
{
    [TestMethod]
    public async Task NativeRekeyJournalExhaustion_PreservesTheOriginal_AndCanRetryWithAvailableCapacity()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy: true);
        await using (var image = OpenMigrationImage(paths.DatabasePath, legacy: true))
        {
            await image.OpenAsync();
            await ExecuteAsync(image, "UPDATE MigrationItems SET Value = zeroblob(1048576) WHERE Id = 2;");
        }
        var original = await File.ReadAllBytesAsync(paths.DatabasePath);
        var policy = new DatabaseMigrationStoragePolicy(4 * 1024 * 1024, _ => long.MaxValue,
            new(JournalBytes: 64 * 1024, TemporaryBytes: 64 * 1024, WalBytes: 64 * 1024));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
                DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword, storagePolicy: policy));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(paths.DatabasePath));
            Assert.IsTrue(Directory.GetFiles(directory.Path).Length <= 3,
                "A failed conversion must reuse the fixed stage/backup slots.");
        }
        Assert.IsTrue(await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
            storagePolicy: new(4 * 1024 * 1024, _ => long.MaxValue)));
        await using var converted = OpenMigrationImage(paths.DatabasePath, legacy: false);
        await converted.OpenAsync();
        Assert.AreEqual(1048576L, await ScalarAsync(converted, "SELECT length(Value) FROM MigrationItems WHERE Id = 2;"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OverLimitSource_PreservesMainAndRecoveryCopiesAcrossRepeatedAdmission(bool legacy)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy);
        var original = await File.ReadAllBytesAsync(paths.DatabasePath);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "interrupted stage");
        await File.WriteAllTextAsync(paths.DatabaseMigrationBackupPath, "retained backup");
        var policy = new DatabaseMigrationStoragePolicy(original.Length - 1, _ => long.MaxValue);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
                DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword, storagePolicy: policy));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(paths.DatabasePath));
            Assert.AreEqual("interrupted stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
            Assert.AreEqual("retained backup", await File.ReadAllTextAsync(paths.DatabaseMigrationBackupPath));
            Assert.AreEqual(3, Directory.GetFiles(directory.Path).Length);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadableHeaderWithInvalidRows_DoesNotAuthorizeDiscardingRecoveryCopies(bool legacy)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy, invalidRows: true);
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "retained candidate");
        var main = await File.ReadAllBytesAsync(paths.DatabasePath);
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);

        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
                storagePolicy: new(64 * 1024, _ => long.MaxValue)));

        CollectionAssert.AreEqual(main, await File.ReadAllBytesAsync(paths.DatabasePath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
        Assert.AreEqual("retained candidate", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
        await AssertMigrationRowsAsync(paths.DatabaseMigrationBackupPath, legacy);
    }

    [TestMethod]
    public async Task InsufficientWorkspace_RefusesBeforeRetiringAnyCopy_AndCanRetryAfterCapacityReturns()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy: true);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "interrupted stage");
        await File.WriteAllTextAsync(paths.DatabaseMigrationBackupPath, "retained backup");
        var original = await File.ReadAllBytesAsync(paths.DatabasePath);
        var policy = new DatabaseMigrationStoragePolicy(64 * 1024, _ => 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
                DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword, storagePolicy: policy));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(paths.DatabasePath));
            Assert.AreEqual("interrupted stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
            Assert.AreEqual("retained backup", await File.ReadAllTextAsync(paths.DatabaseMigrationBackupPath));
            Assert.AreEqual(3, Directory.GetFiles(directory.Path).Length);
        }
        Assert.IsTrue(await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
            storagePolicy: new(64 * 1024, _ => long.MaxValue)));
        await AssertMigrationRowsAsync(paths.DatabasePath, legacy: false);
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationTempPath));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationBackupPath));
    }

    [TestMethod]
    public async Task BackupPromotion_RequiresWorkspaceAdmissionBeforeReplacingAnUnreadableMain()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "unreadable main");
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy: true);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "retained stage");
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
                storagePolicy: new(64 * 1024, _ => 0)));
        Assert.AreEqual("unreadable main", await File.ReadAllTextAsync(paths.DatabasePath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
        Assert.AreEqual("retained stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StructurallyInvalidBackup_IsNotPromotedOrDiscarded(bool mainExists)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        if (mainExists) await File.WriteAllTextAsync(paths.DatabasePath, "unreadable main");
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy: true, invalidRows: true);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "retained stage");
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);
        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
        Assert.AreEqual(mainExists, File.Exists(paths.DatabasePath));
        if (mainExists) Assert.AreEqual("unreadable main", await File.ReadAllTextAsync(paths.DatabasePath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
        Assert.AreEqual("retained stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
    }

    [TestMethod]
    public async Task SuccessfulMigrationAndUnchangedRestarts_DoNotAccumulateRecoveryFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy: true);
        var policy = new DatabaseMigrationStoragePolicy(64 * 1024, _ => long.MaxValue);
        Assert.IsTrue(await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword, storagePolicy: policy));
        var committed = await File.ReadAllBytesAsync(paths.DatabasePath);
        for (var restart = 0; restart < 5; restart++)
        {
            Assert.IsFalse(await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword, storagePolicy: policy));
            Assert.IsTrue(new FileInfo(paths.DatabasePath).Length <= policy.MaximumDatabaseBytes);
            CollectionAssert.AreEqual(committed, await File.ReadAllBytesAsync(paths.DatabasePath));
            Assert.AreEqual(1, Directory.GetFiles(directory.Path).Length);
        }
        await AssertMigrationRowsAsync(paths.DatabasePath, legacy: false);
    }

    [TestMethod]
    [DataRow("stage")]
    [DataRow("wal")]
    [DataRow("journal")]
    public async Task MissingMainWithOnlyRecoveryArtifacts_DoesNotDeleteThemOrCreateAnEmptyVault(string kind)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var retainedPath = kind switch
        {
            "stage" => paths.DatabaseMigrationTempPath,
            "wal" => paths.DatabaseWalPath,
            _ => paths.DatabaseJournalPath
        };
        await File.WriteAllTextAsync(retainedPath, "only recovery candidate");
        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
        Assert.IsFalse(File.Exists(paths.DatabasePath));
        Assert.AreEqual("only recovery candidate", await File.ReadAllTextAsync(retainedPath));
        Assert.AreEqual(1, Directory.GetFiles(directory.Path).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerifiedBackupCanRestoreAMissingMain_ThenRetireTheRedundantStage(bool legacy)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "interrupted stage");
        var migrated = await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
            storagePolicy: new(64 * 1024, _ => long.MaxValue));
        Assert.AreEqual(legacy, migrated);
        await AssertMigrationRowsAsync(paths.DatabasePath, legacy: false);
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationBackupPath));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationTempPath));
    }

    [TestMethod]
    [DataRow("-wal")]
    [DataRow("-journal")]
    [DataRow("-shm")]
    public async Task MissingMainWithAmbiguousSidecar_PreservesTheVerifiedBackupAsWell(string suffix)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy: false);
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);
        await File.WriteAllTextAsync(paths.DatabasePath + suffix, "unresolved main sidecar");
        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
        Assert.IsFalse(File.Exists(paths.DatabasePath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
        Assert.AreEqual("unresolved main sidecar", await File.ReadAllTextAsync(paths.DatabasePath + suffix));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task EmptyDatabaseImage_CannotAuthorizeDiscardingAHealthyStagedCandidate(bool mainExists, bool allocatedEmpty)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabaseMigrationTempPath, legacy: false);
        if (mainExists)
        {
            await File.WriteAllBytesAsync(paths.DatabasePath, Array.Empty<byte>());
            await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy: false);
        }
        else
            await File.WriteAllBytesAsync(paths.DatabaseMigrationBackupPath, Array.Empty<byte>());
        if (allocatedEmpty)
        {
            var emptyPath = mainExists ? paths.DatabasePath : paths.DatabaseMigrationBackupPath;
            File.Delete(emptyPath);
            await CreateMigrationImageAsync(emptyPath, legacy: false);
            await using var empty = OpenMigrationImage(emptyPath, legacy: false);
            await empty.OpenAsync();
            await ExecuteAsync(empty, "DROP TABLE MigrationItems;");
        }
        var stage = await File.ReadAllBytesAsync(paths.DatabaseMigrationTempPath);
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);
        await Assert.ThrowsExactlyAsync<DatabaseUnreadableException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
        Assert.AreEqual(mainExists, File.Exists(paths.DatabasePath));
        CollectionAssert.AreEqual(stage, await File.ReadAllBytesAsync(paths.DatabaseMigrationTempPath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
        await AssertMigrationRowsAsync(paths.DatabaseMigrationTempPath, legacy: false);
    }

    [TestMethod]
    public async Task BlockedCheckpoint_PreservesCopiesUntilAReaderGapAllowsVerifiedCleanup()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy: false);
        await CreateMigrationImageAsync(paths.DatabaseMigrationBackupPath, legacy: false);
        var backup = await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "interrupted stage");
        await using var writer = OpenMigrationImage(paths.DatabasePath, legacy: false);
        await writer.OpenAsync();
        await ExecuteAsync(writer, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0;");
        await using var reader = OpenMigrationImage(paths.DatabasePath, legacy: false);
        await reader.OpenAsync();
        using (var snapshot = reader.BeginTransaction(deferred: true))
        {
            using var pin = reader.CreateCommand();
            pin.Transaction = snapshot;
            pin.CommandText = "SELECT COUNT(*) FROM MigrationItems;";
            await pin.ExecuteScalarAsync();
            await ExecuteAsync(writer, "UPDATE MigrationItems SET Value = 'after-reader' WHERE Id = 1;");
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
            CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(paths.DatabaseMigrationBackupPath));
            Assert.AreEqual("interrupted stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
            Assert.IsTrue(new FileInfo(paths.DatabaseWalPath).Length > 0);
        }
        Assert.IsFalse(await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationTempPath));
        Assert.IsFalse(File.Exists(paths.DatabaseMigrationBackupPath));
        using var read = writer.CreateCommand();
        read.CommandText = "SELECT Value FROM MigrationItems WHERE Id = 1;";
        Assert.AreEqual("after-reader", await read.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task WalPagesAboveTheMainImageLimit_CannotBypassMigrationAdmission()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        await CreateMigrationImageAsync(paths.DatabasePath, legacy: false);
        await File.WriteAllTextAsync(paths.DatabaseMigrationTempPath, "interrupted stage");
        await File.WriteAllTextAsync(paths.DatabaseMigrationBackupPath, "retained backup");
        await using var writer = OpenMigrationImage(paths.DatabasePath, legacy: false);
        await writer.OpenAsync();
        await ExecuteAsync(writer, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0;");
        // The journal-mode header change is legitimate; preserve this pre-growth main image.
        var main = await ReadSharedDatabaseImageAsync(paths.DatabasePath);
        await ExecuteAsync(writer, "UPDATE MigrationItems SET Value = zeroblob(131072) WHERE Id = 1;");
        var walBytes = new FileInfo(paths.DatabaseWalPath).Length;
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
            DatabaseEncryptionMigrator.MigrateIfRequiredAsync(paths, TestPassword,
                storagePolicy: new(main.Length, _ => long.MaxValue)));
        CollectionAssert.AreEqual(main, await ReadSharedDatabaseImageAsync(paths.DatabasePath));
        Assert.AreEqual(walBytes, new FileInfo(paths.DatabaseWalPath).Length);
        Assert.AreEqual("interrupted stage", await File.ReadAllTextAsync(paths.DatabaseMigrationTempPath));
        Assert.AreEqual("retained backup", await File.ReadAllTextAsync(paths.DatabaseMigrationBackupPath));
        Assert.AreEqual(131072L, await ScalarAsync(writer, "SELECT length(Value) FROM MigrationItems WHERE Id = 1;"));
    }

    // Only used while this isolated fixture's writer is idle but deliberately kept open.
    // File.ReadAllBytesAsync uses FileShare.Read, which conflicts with SQLite's existing
    // read/write handle on Windows even when the test is not changing the main image.
    private static async Task<byte[]> ReadSharedDatabaseImageAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static SqliteConnection OpenMigrationImage(string path, bool legacy) => new(legacy
        ? CreateLegacySqlCipherConnectionString(path, SqliteOpenMode.ReadWriteCreate)
        : new SqliteConnectionStringBuilder(DatabaseEncryptionMigrator.CreateRuntimeConnectionString(path, TestPassword))
          { Pooling = false }.ToString());

    private static async Task CreateMigrationImageAsync(string path, bool legacy, bool invalidRows = false)
    {
        await using var connection = OpenMigrationImage(path, legacy);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE MigrationItems (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO MigrationItems VALUES (1, 'first row'), (2, 'second row');
            """);
        if (invalidRows)
            await ExecuteAsync(connection, """
                CREATE TABLE InvalidEvidence (Value INTEGER NOT NULL CHECK(Value > 0));
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO InvalidEvidence VALUES (-1);
                """);
    }

    private static async Task AssertMigrationRowsAsync(string path, bool legacy)
    {
        await using var connection = OpenMigrationImage(path, legacy);
        await connection.OpenAsync();
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM MigrationItems;"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM MigrationItems WHERE Id = 1;";
        Assert.AreEqual("first row", await command.ExecuteScalarAsync());
    }
}
