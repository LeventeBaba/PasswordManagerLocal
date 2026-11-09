using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;

namespace PasswordManagerLocal.Common.Tests.Frontend.Localization;

[TestClass]
public sealed class FrontendLocalizationServiceTests
{
    [TestMethod]
    public async Task InitialLanguageLoadsOneValidatedSnapshot()
    {
        await using var service = CreateService(LanguageJson("Hello", "Szia"));

        var result = await service.SetLanguageAsync(AppLanguage.English);

        Assert.AreEqual(FrontendLocalizationChangeStatus.Applied, result.Status);
        Assert.AreEqual(AppLanguage.English, service.ActiveLanguage);
        Assert.AreEqual(1, service.ActiveTranslationCount);
        Assert.AreEqual("Hello", service.GetString("Greeting"));
    }

    [TestMethod]
    public async Task EnglishAndHungarianResourcesHaveMatchingKeysAndPlaceholders()
    {
        var english = await ReadShippedResourceAsync("en_us.json");
        var hungarian = await ReadShippedResourceAsync("hu.json");

        var violations = FrontendTranslationContractValidator.Compare(english, hungarian);

        Assert.AreEqual(
            0,
            violations.Count,
            string.Join(Environment.NewLine, violations.Select(item => $"{item.Kind}: {item.Key}")));
    }

    [TestMethod]
    public void ContractValidationDetectsMissingUnexpectedAndPlaceholderDifferences()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Count"] = "Count: {0} of {1}",
            ["Indexed"] = "Item {0}",
            ["Named"] = "Hello {person}",
            ["OnlyExpected"] = "Expected"
        };
        var candidate = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Count"] = "Count: {0}",
            ["Indexed"] = "Item {1}",
            ["Named"] = "Hello {user}",
            ["OnlyCandidate"] = "Candidate"
        };

        var violations = FrontendTranslationContractValidator.Compare(expected, candidate);

        Assert.IsTrue(violations.Any(item =>
            item.Kind == FrontendTranslationContractViolationKind.MissingKey &&
            item.Key == "OnlyExpected"));
        Assert.IsTrue(violations.Any(item =>
            item.Kind == FrontendTranslationContractViolationKind.UnexpectedKey &&
            item.Key == "OnlyCandidate"));
        Assert.AreEqual(3, violations.Count(item =>
            item.Kind == FrontendTranslationContractViolationKind.PlaceholderMismatch));
        Assert.IsTrue(violations.Any(item =>
            item.Kind == FrontendTranslationContractViolationKind.InvalidFormat &&
            item.Key == "Named"));
    }

    [TestMethod]
    public void ResourceValidationRejectsEmptyValuesAndInvalidFormatting()
    {
        var invalid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Empty"] = "",
            ["Broken"] = "Broken {0"
        };

        var violations = FrontendTranslationContractValidator.ValidateResource(invalid);

        Assert.IsTrue(violations.Any(item =>
            item.Kind == FrontendTranslationContractViolationKind.EmptyValue));
        Assert.IsTrue(violations.Any(item =>
            item.Kind == FrontendTranslationContractViolationKind.InvalidFormat));
        Assert.AreEqual(
            FrontendTranslationContractViolationKind.EmptyResource,
            FrontendTranslationContractValidator.ValidateResource(
                new Dictionary<string, string>()).Single().Kind);
    }

    [TestMethod]
    public async Task SuccessfulReplacementReleasesPriorLanguageFromAuthority()
    {
        await using var service = CreateService(LanguageJson("Hello", "Szia"));
        await service.SetLanguageAsync(AppLanguage.English);

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(FrontendLocalizationChangeStatus.Applied, result.Status);
        Assert.AreEqual(AppLanguage.Hungarian, service.ActiveLanguage);
        Assert.AreEqual("Szia", service.GetString("Greeting"));
        Assert.AreEqual(1, service.ActiveTranslationCount);
    }

    [TestMethod]
    public async Task InvalidJsonAndInvalidDataPreservePreviousSnapshot()
    {
        var loader = new DelegateResourceLoader((language, _) => ValueTask.FromResult<Stream>(
            JsonStream(language == AppLanguage.English
                ? "{\"Greeting\":\"Hello\"}"
                : "{\"Greeting\":\"Broken {0\"}")));
        await using var service = new FrontendLocalizationService(loader);
        await service.SetLanguageAsync(AppLanguage.English);

        var invalidFormat = await service.SetLanguageAsync(AppLanguage.Hungarian);
        loader.Open = (_, _) => ValueTask.FromResult<Stream>(JsonStream("not json"));
        var invalidJson = await service.SetLanguageAsync(AppLanguage.Hungarian);
        loader.Open = (_, _) => ValueTask.FromResult<Stream>(JsonStream("{}"));
        var emptyResource = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(FrontendLocalizationChangeStatus.Failed, invalidFormat.Status);
        Assert.AreEqual(FrontendLocalizationChangeStatus.Failed, invalidJson.Status);
        Assert.AreEqual(FrontendLocalizationChangeStatus.Failed, emptyResource.Status);
        Assert.AreEqual(AppLanguage.English, service.ActiveLanguage);
        Assert.AreEqual("Hello", service.GetString("Greeting"));
    }

    [TestMethod]
    public async Task MissingRuntimeKeyReturnsMarkerAndSafeDiagnostic()
    {
        var diagnostics = new RecordingDiagnostics();
        await using var service = CreateService(LanguageJson("Hello", "Szia"), diagnostics);
        await service.SetLanguageAsync(AppLanguage.English);

        var value = service.GetString("Missing_Key");

        Assert.AreEqual("[Missing_Key]", value);
        Assert.AreEqual(1, diagnostics.MissingKeyCount);
        Assert.AreEqual(AppLanguage.English, diagnostics.LastMissingLanguage);
    }

    [TestMethod]
    public async Task NewestLanguageRequestWinsWhenOlderLoadIsSlow()
    {
        var hungarianEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new DelegateResourceLoader(async (language, cancellationToken) =>
        {
            if (language == AppLanguage.Hungarian)
            {
                hungarianEntered.TrySetResult();
                await neverRelease.Task.WaitAsync(cancellationToken);
            }
            return JsonStream(language == AppLanguage.English
                ? "{\"Greeting\":\"Hello\"}"
                : "{\"Greeting\":\"Szia\"}");
        });
        await using var service = new FrontendLocalizationService(loader);

        var older = service.SetLanguageAsync(AppLanguage.Hungarian);
        await hungarianEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = service.SetLanguageAsync(AppLanguage.English);

        Assert.AreEqual(FrontendLocalizationChangeStatus.Applied, (await newer).Status);
        Assert.AreEqual(FrontendLocalizationChangeStatus.Superseded, (await older).Status);
        Assert.AreEqual(AppLanguage.English, service.ActiveLanguage);
        Assert.AreEqual("Hello", service.GetString("Greeting"));
    }

    [TestMethod]
    public async Task DisposalCancelsLoadAndPreventsLateActivation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new DelegateResourceLoader(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonStream("{\"Greeting\":\"late\"}");
        });
        var service = new FrontendLocalizationService(loader);

        var loading = service.SetLanguageAsync(AppLanguage.English);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.DisposeAsync();
        release.TrySetResult();
        var result = await loading;

        Assert.AreEqual(FrontendLocalizationChangeStatus.Disposed, result.Status);
        Assert.IsFalse(service.HasActiveSnapshot);
        Assert.AreEqual(0, service.ActiveTranslationCount);
    }

    [TestMethod]
    public async Task CallerCancellationDuringLoadPreservesPreviousSnapshot()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new DelegateResourceLoader(async (language, cancellationToken) =>
        {
            if (language == AppLanguage.Hungarian)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return JsonStream(language == AppLanguage.English
                ? "{\"Greeting\":\"Hello\"}"
                : "{\"Greeting\":\"Szia\"}");
        });
        await using var service = new FrontendLocalizationService(loader);
        await service.SetLanguageAsync(AppLanguage.English);
        using var cancellation = new CancellationTokenSource();

        var loading = service.SetLanguageAsync(AppLanguage.Hungarian, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await loading;

        Assert.AreEqual(FrontendLocalizationChangeStatus.Canceled, result.Status);
        Assert.AreEqual(AppLanguage.English, service.ActiveLanguage);
        Assert.AreEqual("Hello", service.GetString("Greeting"));
    }

    [TestMethod]
    public async Task IndependentInstancesDoNotShareProcessStaticTranslations()
    {
        Assert.AreEqual(
            0,
            typeof(FrontendLocalizationService)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Count(field => !field.IsLiteral),
            "Frontend localization must not retain a process-static snapshot or cache.");
        await using var first = CreateService(LanguageJson("first", "első"));
        await using var second = CreateService(LanguageJson("second", "második"));
        await first.SetLanguageAsync(AppLanguage.English);
        await second.SetLanguageAsync(AppLanguage.Hungarian);

        await first.DisposeAsync();

        Assert.IsFalse(first.HasActiveSnapshot);
        Assert.AreEqual("második", second.GetString("Greeting"));
        Assert.AreEqual(AppLanguage.Hungarian, second.ActiveLanguage);
    }

    private static FrontendLocalizationService CreateService(
        IReadOnlyDictionary<AppLanguage, string> resources,
        IFrontendLocalizationDiagnostics? diagnostics = null) =>
        new(new DelegateResourceLoader((language, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(JsonStream(resources[language]));
        }), diagnostics);

    private static IReadOnlyDictionary<AppLanguage, string> LanguageJson(
        string english,
        string hungarian) =>
        new Dictionary<AppLanguage, string>
        {
            [AppLanguage.English] = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Greeting"] = english
            }),
            [AppLanguage.Hungarian] = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Greeting"] = hungarian
            })
        };

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);

    private static async Task<IReadOnlyDictionary<string, string>> ReadShippedResourceAsync(
        string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);
        await using var stream = File.OpenRead(path);
        return await FrontendTranslationResourceParser.ParseAsync(
            stream,
            CancellationToken.None);
    }

    private sealed class DelegateResourceLoader(
        Func<AppLanguage, CancellationToken, ValueTask<Stream>> open) :
        IFrontendLocalizationResourceLoader
    {
        public Func<AppLanguage, CancellationToken, ValueTask<Stream>> Open { get; set; } = open;

        public ValueTask<Stream> OpenAsync(
            AppLanguage language,
            CancellationToken cancellationToken = default) =>
            Open(language, cancellationToken);
    }

    private sealed class RecordingDiagnostics : IFrontendLocalizationDiagnostics
    {
        public int MissingKeyCount { get; private set; }
        public AppLanguage? LastMissingLanguage { get; private set; }

        public void ReportLoadFailure(
            AppLanguage language,
            FrontendLocalizationFailureKind failureKind,
            Type exceptionType)
        {
        }

        public void ReportMissingKey(AppLanguage? language)
        {
            MissingKeyCount++;
            LastMissingLanguage = language;
        }
    }
}
