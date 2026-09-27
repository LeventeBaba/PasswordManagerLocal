using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;
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
    public async Task LanguageAndThemeRoundTripThroughAuthoritativeSchema()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var store = CreateStore(fileSystem);
        var expected = new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.Hungarian,
            Theme = AppThemeMode.Light
        };

        await store.WriteAsync(expected);
        var actual = await store.ReadAsync();

        Assert.AreEqual(expected, actual);
        StringAssert.Contains(fileSystem.GetFile(PreferencesPath()), "\"language\": \"Hungarian\"");
        StringAssert.Contains(fileSystem.GetFile(PreferencesPath()), "\"theme\": \"Light\"");
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{")]
    [DataRow("{\"schemaVersion\":1")]
    [DataRow("{}")]
    [DataRow("{\"language\":\"English\",\"theme\":\"Dark\"}")]
    [DataRow("{\"schemaVersion\":1,\"language\":\"English\"}")]
    [DataRow("{\"schemaVersion\":1,\"language\":0,\"theme\":\"Dark\"}")]
    [DataRow("{\"schemaVersion\":1,\"language\":\"English\",\"theme\":1}")]
    public async Task EmptyMalformedOrTruncatedContentResetsToDefaults(string contents)
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(PreferencesPath(), contents);
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
    }

    [TestMethod]
    public async Task UnsupportedSchemaVersionResetsToDefaults()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(
            PreferencesPath(),
            "{\"schemaVersion\":2,\"language\":\"Hungarian\",\"theme\":\"Light\"}");
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
    }

    [DataTestMethod]
    [DataRow("Klingon", "Dark")]
    [DataRow("99", "Dark")]
    [DataRow("English", "FluentCustom")]
    [DataRow("English", "99")]
    public async Task InvalidEnumValuesResetSafely(string language, string theme)
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(
            PreferencesPath(),
            $"{{\"schemaVersion\":1,\"language\":\"{language}\",\"theme\":\"{theme}\"}}");
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
    }

    [TestMethod]
    public async Task OldMixedConfigurationShapeIsUnsupportedAndReset()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(
            PreferencesPath(),
            "{\"language\":\"Hungarian\",\"theme\":\"Light\",\"windowsFirewallConfigured\":true}");
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        Assert.IsFalse(fileSystem.GetFile(PreferencesPath()).Contains(
            "windowsFirewallConfigured",
            StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task ObsoleteAdditionalFieldsMakeTheSchemaUnsupported()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        fileSystem.SetFile(
            PreferencesPath(),
            "{\"schemaVersion\":1,\"language\":\"English\",\"theme\":\"Dark\",\"windowsFirewallConfigured\":true}");
        var store = CreateStore(fileSystem);

        var preferences = await store.ReadAsync();

        Assert.AreEqual(ApplicationPreferencesDefaults.Create(), preferences);
        Assert.IsFalse(fileSystem.GetFile(PreferencesPath()).Contains(
            "windowsFirewallConfigured",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AtomicSaveLeavesReadableFinalFileAndNoTemporaryFile()
    {
        var fileSystem = new FakeApplicationPreferencesFileSystem();
        var store = CreateStore(fileSystem);
        var expected = new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark
        };

        await store.WriteAsync(expected);

        Assert.AreEqual(0, fileSystem.TemporaryFileCount);
        AssertValidCurrentSchema(fileSystem.GetFile(PreferencesPath()));
        Assert.AreEqual(expected, await store.ReadAsync());
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
            Theme = AppThemeMode.Dark
        };
        await store.WriteAsync(original);
        fileSystem.ReplaceFailure = new IOException("replacement failed");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.WriteAsync(original with
            {
                Language = AppLanguage.Hungarian,
                Theme = AppThemeMode.Light
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
                Theme = AppThemeMode.Dark
            };
            var second = new ApplicationPreferences
            {
                SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
                Language = AppLanguage.Hungarian,
                Theme = AppThemeMode.Light
            };

            var writes = Enumerable.Range(0, 12)
                .Select(index => (index & 1) == 0
                    ? firstStore.WriteAsync(first)
                    : secondStore.WriteAsync(second));
            await Task.WhenAll(writes);

            var actual = await new FileApplicationPreferencesStore(directory).ReadAsync();
            Assert.IsTrue(actual == first || actual == second);
            AssertValidCurrentSchema(await File.ReadAllTextAsync(Path.Combine(
                directory,
                ApplicationFileNames.ApplicationPreferencesFileName)));
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
    }
}
