using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
[TestCategory("StorageAuxiliary")]
public sealed class DbConfigManagerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InterruptedResetNeverGeneratesOrReplacesAKey(bool hasConfiguration)
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        if (hasConfiguration) _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        var original = hasConfiguration ? File.ReadAllBytes(paths.DatabaseConfigPath) : [];
        File.WriteAllBytes(paths.DatabaseResetPendingPath, []);
        Assert.ThrowsExactly<DatabaseResetIncompleteException>(() =>
            DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
        Assert.AreEqual(hasConfiguration, File.Exists(paths.DatabaseConfigPath));
        if (hasConfiguration) CollectionAssert.AreEqual(original, File.ReadAllBytes(paths.DatabaseConfigPath));
        Assert.IsTrue(File.Exists(paths.DatabaseResetPendingPath));
    }

    [TestMethod]
    public void LostPlatformKey_IsSeparateFromVersionFailureAndPreservesTheConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        var original = File.ReadAllBytes(paths.DatabaseConfigPath);
        protector.IsPermanentlyUnavailable = true;

        Assert.ThrowsExactly<DatabaseKeyUnavailableException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, protector));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(paths.DatabaseConfigPath));
    }

    [TestMethod]
    public void MissingConfigurationForExistingDatabase_DoesNotCreateAReplacementKey()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        File.WriteAllText(paths.DatabasePath, "existing encrypted data");
        File.WriteAllText($"{paths.DatabaseConfigPath}.tmp", "possible interrupted key");
        var legacy = $"{paths.DatabaseConfigPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(legacy, "possible legacy key");

        Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, new SwitchableKeyProtector()));

        Assert.IsFalse(File.Exists(paths.DatabaseConfigPath));
        Assert.AreEqual("existing encrypted data", File.ReadAllText(paths.DatabasePath));
        Assert.AreEqual("possible interrupted key", File.ReadAllText($"{paths.DatabaseConfigPath}.tmp"));
        Assert.AreEqual("possible legacy key", File.ReadAllText(legacy));
    }

    [TestMethod]
    public void DamagedHeader_IsSeparateFromAnUnsupportedVersion()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        var bytes = File.ReadAllBytes(paths.DatabaseConfigPath);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(paths.DatabaseConfigPath, bytes);

        Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(paths.DatabaseConfigPath));
    }

    [TestMethod]
    public void UnsupportedVersion_PreservesItsVersionMetadata()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        var bytes = File.ReadAllBytes(paths.DatabaseConfigPath);
        var version = PasswordManagerLocal.Common.Backend.Constants.DatabaseConstants.CurrentDbVersion + 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(PasswordManagerLocal.Common.Backend.Constants.DatabaseConstants.DbConfigVersionOffset, sizeof(int)),
            version);
        File.WriteAllBytes(paths.DatabaseConfigPath, bytes);

        var failure = Assert.ThrowsExactly<DatabaseVersionNotSupportedException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, protector));

        Assert.AreEqual(version, failure.DetectedVersion);
    }

    [TestMethod]
    public void InvalidUnwrappedKeyLength_IsReportedAsAnUnavailableKey()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        var bytes = File.ReadAllBytes(paths.DatabaseConfigPath);
        File.WriteAllBytes(paths.DatabaseConfigPath, bytes[..^1]);

        Assert.ThrowsExactly<DatabaseKeyUnavailableException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
    }

    [TestMethod]
    public void TemporaryKeyUnavailability_IsNotTranslatedToDatabaseCompatibilityFailure()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();

        _ = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        protector.IsTemporarilyUnavailable = true;

        Assert.Throws<KeyProtectorUnavailableException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
    }

    [TestMethod]
    public async Task ConcurrentInitialization_ProducesOneAuthoritativeKeyAndOneEmptyLock()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var passwords = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            DbConfigManager.GetOrCreateDatabasePassword(paths, new SwitchableKeyProtector()))));
        Assert.AreEqual(1, passwords.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(2, Directory.GetFiles(directory.Path).Length);
        Assert.AreEqual(0L, new FileInfo($"{paths.DatabaseConfigPath}.lock").Length);
        Assert.IsTrue(new FileInfo(paths.DatabaseConfigPath).Length <= StorageCapacityConstants.MaxDatabaseConfigurationBytes);
    }

    [TestMethod]
    public void MissingKeyWithOnlyDatabaseRecoveryArtifacts_DoesNotGenerateAReplacement()
    {
        for (var artifact = 0; artifact < 8; artifact++)
        {
            using var directory = new TemporaryDirectory();
            var paths = new BackendStoragePaths(directory.Path);
            var recoveryPath = new[] { paths.DatabaseMigrationBackupPath, paths.DatabaseMigrationTempPath,
                paths.DatabaseWalPath, paths.DatabaseShmPath, paths.DatabaseJournalPath,
                $"{paths.DatabaseMigrationBackupPath}-wal", $"{paths.DatabaseMigrationTempPath}-journal",
                $"{paths.DatabaseMigrationTempPath}-shm" }[artifact];
            File.WriteAllText(recoveryPath, "existing recovery evidence");
            File.WriteAllText($"{paths.DatabaseConfigPath}.tmp", "possible recovery key");

            Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(() =>
                DbConfigManager.GetOrCreateDatabasePassword(paths, new SwitchableKeyProtector()));

            Assert.IsFalse(File.Exists(paths.DatabaseConfigPath));
            Assert.AreEqual("existing recovery evidence", File.ReadAllText(recoveryPath));
            Assert.AreEqual("possible recovery key", File.ReadAllText($"{paths.DatabaseConfigPath}.tmp"));
        }
    }

    [TestMethod]
    public async Task RestartCleanup_ReclaimsOwnedSlotsOnlyAfterTheCommittedKeyIsVerified()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();
        var password = DbConfigManager.GetOrCreateDatabasePassword(paths, protector);
        SQLitePCL.Batteries_V2.Init();
        var factory = new SqliteConnectionFactory(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            DatabaseEncryptionMigrator.CreateRuntimeConnectionString(paths.DatabasePath, password))
        {
            Pooling = false
        }.ToString());
        await using (var connection = factory.CreateConnection())
            await AppDatabaseInitializer.InitializeAsync(connection);
        var original = File.ReadAllBytes(paths.DatabaseConfigPath);
        var legacy = $"{paths.DatabaseConfigPath}.{Guid.NewGuid():N}.tmp";
        var unrelated = $"{paths.DatabaseConfigPath}.notes.tmp";
        File.WriteAllText(unrelated, "keep");
        for (var attempt = 0; attempt < 32; attempt++)
        {
            File.WriteAllText($"{paths.DatabaseConfigPath}.tmp", "interrupted");
            File.WriteAllText(legacy, "interrupted legacy");
            protector.IsTemporarilyUnavailable = true;
            Assert.Throws<KeyProtectorUnavailableException>(() =>
                DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
            Assert.IsTrue(File.Exists(legacy));
            Assert.IsTrue(File.Exists($"{paths.DatabaseConfigPath}.tmp"));
            protector.IsTemporarilyUnavailable = false;
            Assert.AreEqual(password, DbConfigManager.GetOrCreateDatabasePassword(paths, protector));
            Assert.IsTrue(File.Exists(legacy), "Unwrapping a key is insufficient proof for discarding recovery files.");
            DbConfigManager.TryCleanupTemporaryConfigurationsForVerifiedDatabase(paths);
            Assert.IsFalse(File.Exists(legacy));
            Assert.IsFalse(File.Exists($"{paths.DatabaseConfigPath}.tmp"));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(paths.DatabaseConfigPath));
            Assert.AreEqual(1, Directory.GetFiles(directory.Path, "*.tmp").Length);
        }
        Assert.AreEqual("keep", File.ReadAllText(unrelated));
    }

    [TestMethod]
    public void OversizedConfiguration_IsRejectedWithoutDiscardingPossibleRecoveryFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var bytes = new byte[StorageCapacityConstants.MaxDatabaseConfigurationBytes + 1];
        File.WriteAllBytes(paths.DatabaseConfigPath, bytes);
        File.WriteAllText($"{paths.DatabaseConfigPath}.tmp", "possible key");
        Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(() =>
            DbConfigManager.GetOrCreateDatabasePassword(paths, new SwitchableKeyProtector()));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(paths.DatabaseConfigPath));
        Assert.AreEqual("possible key", File.ReadAllText($"{paths.DatabaseConfigPath}.tmp"));
    }

    [TestMethod]
    public void OversizedProtectedKey_IsRejectedBeforeAnyStagingFileIsWritten()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(() =>
                DbConfigManager.GetOrCreateDatabasePassword(paths, new OversizedKeyProtector()));
            Assert.IsFalse(File.Exists(paths.DatabaseConfigPath));
            Assert.IsFalse(File.Exists($"{paths.DatabaseConfigPath}.tmp"));
            Assert.AreEqual(1, Directory.GetFiles(directory.Path).Length);
        }
    }

    private sealed class OversizedKeyProtector : IKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => new byte[StorageCapacityConstants.MaxDatabaseConfigurationBytes];
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PasswordManagerLocal.DbConfig.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
