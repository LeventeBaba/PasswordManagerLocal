using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
public sealed class DbConfigManagerTests
{
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

        Assert.ThrowsExactly<DatabaseConfigurationInvalidException>(
            () => DbConfigManager.GetOrCreateDatabasePassword(paths, new SwitchableKeyProtector()));

        Assert.IsFalse(File.Exists(paths.DatabaseConfigPath));
        Assert.AreEqual("existing encrypted data", File.ReadAllText(paths.DatabasePath));
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
