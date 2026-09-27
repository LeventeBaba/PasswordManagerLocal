using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.IPC.Localization;

[TestClass]
public sealed class AgentLocalizationTests
{
    [DataTestMethod]
    [DataRow(AppLanguage.English)]
    [DataRow(AppLanguage.Hungarian)]
    public async Task EmbeddedResourceLoadsForEverySupportedLanguage(AppLanguage language)
    {
        var localizer = await AgentLocalizer.CreateAsync(language);

        Assert.AreEqual(language, localizer.CurrentLanguage);
        foreach (var key in AgentLocalizationKeys.All)
            Assert.IsFalse(localizer.GetString(key).StartsWith("[", StringComparison.Ordinal), key);
    }

    [TestMethod]
    public async Task EmbeddedResourcesHaveIdenticalKeysPlaceholdersAndNewlineUsage()
    {
        var loader = new EmbeddedAgentLocalizationResourceLoader();
        await using var englishStream = loader.Open(AppLanguage.English);
        await using var hungarianStream = loader.Open(AppLanguage.Hungarian);
        var english = await AgentLocalizationResourceParser.ParseAsync(englishStream);
        var hungarian = await AgentLocalizationResourceParser.ParseAsync(hungarianStream);

        AgentLocalizationResourceSetValidator.ValidateEquivalent(english, hungarian);
        CollectionAssert.AreEquivalent(english.Keys.ToArray(), hungarian.Keys.ToArray());
    }

    [TestMethod]
    public async Task DuplicateJsonKeysAreRejected()
    {
        var translations = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid");
        var json = JsonSerializer.Serialize(translations);
        json = json[..^1] + ",\"Agent.Tray.Tooltip\":\"duplicate\"}";

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await ParseAsync(json));
    }

    [TestMethod]
    public async Task MissingUnknownEmptyAndNonStringValuesAreRejected()
    {
        var missing = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid");
        missing.Remove(AgentLocalizationKeys.TrayOpen);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await ParseAsync(JsonSerializer.Serialize(missing)));

        var unknown = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid");
        unknown["Agent.Unknown"] = "unexpected";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await ParseAsync(JsonSerializer.Serialize(unknown)));

        var empty = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid");
        empty[AgentLocalizationKeys.TrayOpen] = "   ";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await ParseAsync(JsonSerializer.Serialize(empty)));

        var nonStringJson = JsonSerializer.Serialize(
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid"));
        nonStringJson = nonStringJson.Replace(
            $"\"{AgentLocalizationKeys.TrayOpen}\":\"valid:{AgentLocalizationKeys.TrayOpen}\"",
            $"\"{AgentLocalizationKeys.TrayOpen}\":42",
            StringComparison.Ordinal);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await ParseAsync(nonStringJson));
    }

    [TestMethod]
    public async Task InvalidCompositeFormattingIsRejected()
    {
        var translations = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("valid");
        translations[AgentLocalizationKeys.TrayOpen] = "Broken {";

        await Assert.ThrowsExactlyAsync<FormatException>(
            async () => await ParseAsync(JsonSerializer.Serialize(translations)));
    }

    [TestMethod]
    public void PlaceholderIdentityAndNewlineMismatchAreRejected()
    {
        var english = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("en");
        var hungarian = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("hu");
        english[AgentLocalizationKeys.UiLaunchFailed] = "Failed to open {0}.";
        hungarian[AgentLocalizationKeys.UiLaunchFailed] = "Nem sikerült megnyitni: {1}.";

        Assert.ThrowsExactly<InvalidDataException>(
            () => AgentLocalizationResourceSetValidator.ValidateEquivalent(english, hungarian));

        hungarian[AgentLocalizationKeys.UiLaunchFailed] = "Nem sikerült megnyitni: {0}.\nPróbáld újra.";
        Assert.ThrowsExactly<InvalidDataException>(
            () => AgentLocalizationResourceSetValidator.ValidateEquivalent(english, hungarian));
    }

    [TestMethod]
    public async Task SwitchingReplacesTheSingleActiveSnapshot()
    {
        var loader = new InMemoryAgentLocalizationResourceLoader();
        loader.SetResource(
            AppLanguage.English,
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("en"));
        loader.SetResource(
            AppLanguage.Hungarian,
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("hu"));
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var activeField = typeof(AgentLocalizer).GetField(
            "_active",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstSnapshot = activeField.GetValue(localizer);

        var candidate = await localizer.LoadCandidateAsync(AppLanguage.Hungarian);
        localizer.Activate(candidate);
        var secondSnapshot = activeField.GetValue(localizer);

        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);
        Assert.AreEqual($"hu:{AgentLocalizationKeys.TrayOpen}", localizer.GetString(AgentLocalizationKeys.TrayOpen));
        Assert.AreNotSame(firstSnapshot, secondSnapshot);
        var fields = typeof(AgentLocalizer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.AreEqual(1, fields.Count(field => field.Name == "_active"));
        Assert.IsFalse(fields.Any(field =>
            field.FieldType.IsGenericType &&
            field.FieldType.GetGenericArguments().Contains(typeof(AppLanguage))));
    }

    [TestMethod]
    public async Task FailedReplacementLeavesPreviousLanguageActive()
    {
        var loader = new InMemoryAgentLocalizationResourceLoader();
        loader.SetResource(
            AppLanguage.English,
            InMemoryAgentLocalizationResourceLoader.CreateValidTranslations("en"));
        loader.SetFailure(AppLanguage.Hungarian, new IOException("resource unavailable"));
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);

        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await localizer.LoadCandidateAsync(AppLanguage.Hungarian));

        Assert.AreEqual(AppLanguage.English, localizer.CurrentLanguage);
        Assert.AreEqual($"en:{AgentLocalizationKeys.TrayExit}", localizer.GetString(AgentLocalizationKeys.TrayExit));
    }

    [TestMethod]
    public async Task MissingRuntimeKeyReturnsVisibleMarker()
    {
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English);
        Assert.AreEqual("[Agent.Does.Not.Exist]", localizer.GetString("Agent.Does.Not.Exist"));
    }

    private static async Task<IReadOnlyDictionary<string, string>> ParseAsync(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
        return await AgentLocalizationResourceParser.ParseAsync(stream);
    }
}
