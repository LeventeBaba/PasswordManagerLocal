using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Configuration.BackgroundSync;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Configuration;

[TestClass]
[TestCategory("StorageAuxiliary")]
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

    [TestMethod]
    public async Task RepeatedFailedReplacementAndCleanup_ReusesOneSlotAcrossStoreRecreation()
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem
        {
            ReplaceFailure = new IOException("replace"),
            DeleteFailure = new IOException("cleanup")
        };
        const string original = "{\"isEnabled\":true}";
        fileSystem.SetFile(SettingsPath(), original);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                CreateStore(fileSystem).WriteAsync(new BackgroundSyncSettings(false)));
            Assert.AreEqual(original, fileSystem.ReadAllText(SettingsPath()));
            Assert.AreEqual(1, fileSystem.TemporaryFileCount);
        }
        Assert.AreEqual(1, fileSystem.WrittenTemporaryPaths.Count);
        fileSystem.ReplaceFailure = null;
        fileSystem.DeleteFailure = null;
        await CreateStore(fileSystem).WriteAsync(new BackgroundSyncSettings(false));
        Assert.IsFalse((await CreateStore(fileSystem).ReadAsync()).IsEnabled);
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }

    [TestMethod]
    public async Task OrphanCleanup_ReclaimsOnlyOwnedSlotsAndPreservesTheCommittedSetting()
    {
        var fileSystem = new FakeBackgroundSyncSettingsFileSystem();
        fileSystem.SetFile(SettingsPath(), "{\"isEnabled\":true}");
        fileSystem.SetFile($"{SettingsPath()}.tmp", "interrupted");
        var legacy = $"{SettingsPath()}.{Guid.NewGuid():N}.tmp";
        var unrelated = $"{SettingsPath()}.notes.tmp";
        fileSystem.SetFile(legacy, "interrupted legacy");
        fileSystem.SetFile(unrelated, "user file");
        fileSystem.ReplaceFailure = new IOException("replace");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            CreateStore(fileSystem).WriteAsync(new BackgroundSyncSettings(false)));

        Assert.IsTrue((await CreateStore(fileSystem).ReadAsync()).IsEnabled);
        Assert.IsFalse(fileSystem.FileExists(legacy));
        Assert.IsFalse(fileSystem.FileExists($"{SettingsPath()}.tmp"));
        Assert.AreEqual("user file", fileSystem.ReadAllText(unrelated));
    }

    [TestMethod]
    public async Task CancelledContender_DoesNotTouchTheLockOwnersStagingSlot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-background-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ApplicationFileNames.BackgroundSyncSettingsFileName);
        try
        {
            var fileSystem = new PhysicalBackgroundSyncSettingsFileSystem();
            await using (await fileSystem.AcquireExclusiveLockAsync($"{path}.lock", CancellationToken.None))
            {
                await File.WriteAllTextAsync($"{path}.tmp", "owned by another writer");
                using var cts = new CancellationTokenSource();
                var pending = new FileBackgroundSyncSettingsStore(directory).WriteAsync(new(false), cts.Token);
                Assert.IsFalse(pending.IsCompleted);
                cts.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                Assert.AreEqual("owned by another writer", await File.ReadAllTextAsync($"{path}.tmp"));
                Assert.IsFalse(File.Exists(path));
            }
            await new FileBackgroundSyncSettingsStore(directory).WriteAsync(new(true));
            Assert.IsTrue((await new FileBackgroundSyncSettingsStore(directory).ReadAsync()).IsEnabled);
            Assert.IsFalse(File.Exists($"{path}.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task PhysicalConcurrentStoreInstances_LeaveOnlyTheCurrentSettingAndEmptyLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-background-concurrent-{Guid.NewGuid():N}");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
                new FileBackgroundSyncSettingsStore(directory).WriteAsync(new((index & 1) == 0)))));
            _ = await new FileBackgroundSyncSettingsStore(directory).ReadAsync();
            var files = Directory.GetFiles(directory);
            Assert.AreEqual(2, files.Length);
            Assert.AreEqual(0L, new FileInfo(files.Single(path => path.EndsWith(".lock", StringComparison.Ordinal))).Length);
            Assert.IsTrue(files.All(path => new FileInfo(path).Length < 1024));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
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
