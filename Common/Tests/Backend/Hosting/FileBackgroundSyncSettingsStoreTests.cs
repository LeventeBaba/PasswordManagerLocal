using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class FileBackgroundSyncSettingsStoreTests
{
    [TestMethod]
    public async Task MissingSettingFileReturnsDisabledDefault()
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        var store = CreateStore(fileSystem);

        var settings = await store.ReadAsync();

        Assert.IsFalse(settings.IsEnabled);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ValidSettingReturnsPersistedValue(bool isEnabled)
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        fileSystem.SetFile(SettingsPath(), $"{{\"isEnabled\":{isEnabled.ToString().ToLowerInvariant()}}}");
        var store = CreateStore(fileSystem);

        var settings = await store.ReadAsync();

        Assert.AreEqual(isEnabled, settings.IsEnabled);
    }

    [DataTestMethod]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("{\"isEnabled\":null}")]
    [DataRow("{\"isEnabled\":1}")]
    public async Task MalformedOrSemanticallyInvalidSettingFailsSafely(string contents)
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        fileSystem.SetFile(SettingsPath(), contents);
        var store = CreateStore(fileSystem);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadAsync());
    }

    [TestMethod]
    public async Task AtomicReplacementFailurePreservesOldAuthoritativeValue()
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        fileSystem.SetFile(SettingsPath(), "{\"isEnabled\":false}");
        fileSystem.ReplaceFailure = new IOException("replacement failed");
        var store = CreateStore(fileSystem);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.WriteAsync(new BackgroundSyncSettings(true)));
        fileSystem.ReplaceFailure = null;
        var settings = await store.ReadAsync();

        Assert.IsFalse(settings.IsEnabled);
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }

    [TestMethod]
    public async Task RepeatedWriteOfSameValueIsIdempotent()
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        var store = CreateStore(fileSystem);

        await store.WriteAsync(new BackgroundSyncSettings(true));
        await store.WriteAsync(new BackgroundSyncSettings(true));
        var settings = await store.ReadAsync();

        Assert.IsTrue(settings.IsEnabled);
        Assert.AreEqual(2, fileSystem.ReplaceCount);
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }

    private static FileBackgroundSyncSettingsStore CreateStore(
        FakeBackgroundSyncSettingsFileSystem fileSystem) =>
        new(ApplicationDataDirectory(), fileSystem);

    private static string SettingsPath() => Path.Combine(
        ApplicationDataDirectory(),
        ApplicationFileNames.BackgroundSyncSettingsFileName);

    private static string ApplicationDataDirectory() => Path.Combine(
        Path.GetTempPath(),
        "PasswordManagerLocal-BackgroundSyncSettingsStoreTests");
}
