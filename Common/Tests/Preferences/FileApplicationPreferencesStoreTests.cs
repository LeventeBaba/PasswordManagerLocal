using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Globalization;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Tests.Preferences;

[TestClass]
public sealed class FileApplicationPreferencesStoreTests
{
    [TestMethod]
    public async Task MissingFileReturnsAndPersistsDocumentedDefaults()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();
        var expected = ApplicationPreferencesDefaults.Create();

        Assert.AreEqual(expected, preferences);
        Assert.IsTrue(fileSystem.FileExists(PreferencesPath()));
        Assert.AreEqual(1, fileSystem.ReplaceCount);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTripsTheCurrentSchemaWithoutLeavingTemporaryFiles()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var store = CreateStore(fileSystem);
        var expected = new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.Hungarian,
            Theme = AppThemeId.Light,
            InterfaceAnimationsEnabled = false
        };

        await store.WriteAsync(expected);
        var actual = await store.ReadAsync();

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
        StringAssert.Contains(fileSystem.GetFile(PreferencesPath()), "\"language\": \"Hungarian\"");
        StringAssert.Contains(fileSystem.GetFile(PreferencesPath()), "\"theme\": \"Light\"");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":"Light"}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":"Light","interfaceAnimationsEnabled":false,"unexpected":true}""")]
    [DataRow("""{"schemaVersion":0,"language":"Hungarian","theme":"Light","interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Klingon","theme":"Light","interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":0,"theme":"Light","interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":"Ocean","interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":"1","interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":1,"interfaceAnimationsEnabled":false}""")]
    [DataRow("""{"schemaVersion":VERSION,"language":"Hungarian","theme":"Light","interfaceAnimationsEnabled":0}""")]
    public async Task InvalidPreferencesAreReplacedWithCurrentDefaults(string contents)
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var schemaVersion = ApplicationPreferences.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);
        fileSystem.SetFile(PreferencesPath(), contents.Replace("VERSION", schemaVersion, StringComparison.Ordinal));
        var store = CreateStore(fileSystem);
        var expected = ApplicationPreferencesDefaults.Create();

        Assert.AreEqual(expected, await store.ReadAsync());

        Assert.AreEqual(1, fileSystem.ReplaceCount);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
        Assert.AreEqual(expected, await store.ReadAsync());
        Assert.AreEqual(1, fileSystem.ReplaceCount, "The replacement must be readable without another reset.");
    }

    [TestMethod]
    public async Task FailedReplacementPreservesPreviouslyValidDestination()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var store = CreateStore(fileSystem);
        var original = new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeId.Dark
        };
        await store.WriteAsync(original);
        fileSystem.ReplaceFailure = new IOException("replacement failed");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.WriteAsync(original with
            {
                Language = AppLanguage.Hungarian,
                Theme = AppThemeId.Light
            }));
        fileSystem.ReplaceFailure = null;

        Assert.AreEqual(original, await store.ReadAsync());
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }



    [TestMethod]
    public async Task UnavailablePreferenceFileIsReportedAndReturnsCentralDefaults()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem
        {
            LockFailure = new IOException("lock unavailable")
        };
        var diagnostics = new RecordingApplicationPreferencesDiagnostics();
        var store = new FileApplicationPreferencesStore(
            ApplicationDataDirectory(),
            fileSystem,
            diagnostics);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        Assert.AreEqual(1, diagnostics.Failures.Count);
        Assert.AreEqual("read-unavailable", diagnostics.Failures[0].Operation);
        Assert.AreEqual(typeof(IOException), diagnostics.Failures[0].ExceptionType);
    }

    [TestMethod]
    public async Task FailedInvalidFileResetIsReportedAndStillReturnsDefaults()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(PreferencesPath(), "{");
        fileSystem.ReplaceFailure = new IOException("replacement failed");
        var diagnostics = new RecordingApplicationPreferencesDiagnostics();
        var store = new FileApplicationPreferencesStore(
            ApplicationDataDirectory(),
            fileSystem,
            diagnostics);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        Assert.AreEqual(1, diagnostics.Failures.Count);
        Assert.AreEqual("reset-invalid", diagnostics.Failures[0].Operation);
        Assert.AreEqual(typeof(IOException), diagnostics.Failures[0].ExceptionType);
        Assert.AreEqual("{", fileSystem.GetFile(PreferencesPath()));
        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
    }

    [TestMethod]
    public async Task PhysicalConcurrentWritersAlwaysLeaveACompleteReadableSchema()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PasswordManagerLocal-ApplicationPreferences-{Guid.NewGuid():N}");
        try
        {
            var firstStore = new FileApplicationPreferencesStore(directory);
            var secondStore = new FileApplicationPreferencesStore(directory);
            var first = new ApplicationPreferences
            {
                SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
                Language = AppLanguage.English,
                Theme = AppThemeId.Dark,
                InterfaceAnimationsEnabled = false
            };
            var second = new ApplicationPreferences
            {
                SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
                Language = AppLanguage.Hungarian,
                Theme = AppThemeId.Light,
                InterfaceAnimationsEnabled = false
            };

            var writes = Enumerable.Range(0, 12)
                .Select(index => (index & 1) == 0
                    ? firstStore.WriteAsync(first)
                    : secondStore.WriteAsync(second));
            await Task.WhenAll(writes);

            AssertValidCurrentSchema(await File.ReadAllTextAsync(Path.Combine(
                directory,
                ApplicationFileNames.ApplicationPreferencesFileName)));
            var actual = await new FileApplicationPreferencesStore(directory).ReadAsync();
            Assert.IsTrue(actual == first || actual == second);
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly).Count());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static FileApplicationPreferencesStore CreateStore(
        FakeApplicationPreferencesFileSystem fileSystem) =>
        new(ApplicationDataDirectory(), fileSystem);

    private static string PreferencesPath() => Path.Combine(
        ApplicationDataDirectory(),
        ApplicationFileNames.ApplicationPreferencesFileName);

    private static string ApplicationDataDirectory() => Path.Combine(
        Path.GetTempPath(),
        "PasswordManagerLocal-ApplicationPreferencesStoreTests");

    private static void AssertValidCurrentSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(
            ApplicationPreferences.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(JsonValueKind.String, root.GetProperty("language").ValueKind);
        Assert.AreEqual(JsonValueKind.String, root.GetProperty("theme").ValueKind);
        Assert.IsTrue(root.GetProperty("theme").GetString() is "Dark" or "Light");
        Assert.IsTrue(root.GetProperty("interfaceAnimationsEnabled").ValueKind is
            JsonValueKind.True or JsonValueKind.False);
    }
}
