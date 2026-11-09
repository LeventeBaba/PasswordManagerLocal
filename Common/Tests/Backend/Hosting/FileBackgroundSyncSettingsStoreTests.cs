using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;
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

    [TestMethod]
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

    [TestMethod]
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
        const string savedSetting = "{\"isEnabled\":true}";
        fileSystem.SetFile(SettingsPath(), savedSetting);
        fileSystem.ReplaceFailure = new IOException("replacement failed");
        var store = CreateStore(fileSystem);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.WriteAsync(new BackgroundSyncSettings(false)));
        Assert.AreEqual(savedSetting, fileSystem.ReadAllText(SettingsPath()));
        Assert.IsTrue((await store.ReadAsync()).IsEnabled);
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WritePersistsAChangedValueWithoutLeavingTemporaryFiles(bool isEnabled)
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        var store = CreateStore(fileSystem);
        fileSystem.SetFile(SettingsPath(), isEnabled
            ? """{"isEnabled":false}"""
            : """{"isEnabled":true}""");

        await store.WriteAsync(new BackgroundSyncSettings(isEnabled));

        Assert.IsTrue(fileSystem.FileExists(SettingsPath()));
        Assert.AreEqual(isEnabled, (await CreateStore(fileSystem).ReadAsync()).IsEnabled);
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
