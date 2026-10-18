using System.Text;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public static class TestUiPreferencesFactory
{
    public static async Task<UiPreferencesService> CreateAsync(
        ApplicationPreferences? preferences = null,
        IApplicationPreferencesStore? store = null,
        IApplicationPreferencesChangeNotifier? notifier = null,
        IFrontendLocalizationResourceLoader? resourceLoader = null)
    {
        var initial = preferences ?? (store is null
            ? CreateDefaultPreferences()
            : await store.ReadAsync());
        var resolvedStore = store ?? new InMemoryApplicationPreferencesStore(initial);
        return await UiPreferencesService.CreateAsync(
            resolvedStore,
            notifier,
            initialPreferences: initial,
            localizationResourceLoader: resourceLoader ?? new MinimalLocalizationResourceLoader());
    }

    public static ApplicationPreferences CreateDefaultPreferences() =>
        new()
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark,
            InterfaceAnimationsEnabled = true
        };

    private sealed class MinimalLocalizationResourceLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(
            AppLanguage language,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = language switch
            {
                AppLanguage.English => "English",
                AppLanguage.Hungarian => "Magyar",
                _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
            };
            return ValueTask.FromResult<Stream>(new MemoryStream(
                Encoding.UTF8.GetBytes($"{{\"Test_Label\":\"{text}\"}}"),
                writable: false));
        }
    }
}
