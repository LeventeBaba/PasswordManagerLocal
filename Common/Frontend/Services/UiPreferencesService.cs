using Avalonia;
using Avalonia.Styling;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class UiPreferencesService
{
    private readonly IApplicationPreferencesStore _store;
    private readonly IApplicationPreferencesChangeNotifier? _changeNotifier;
    private ApplicationPreferences _current;

    public UiPreferencesService(
        IApplicationPreferencesStore store,
        IApplicationPreferencesChangeNotifier? changeNotifier = null,
        FrontendPlatformServices? platformServices = null,
        ApplicationPreferences? initialPreferences = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _changeNotifier = changeNotifier;
        PlatformServices = platformServices ?? new FrontendPlatformServices();
        _current = initialPreferences ?? _store.ReadAsync().GetAwaiter().GetResult();
        MotionPolicy = new InterfaceMotionPolicy(PlatformServices, _current.InterfaceAnimationsEnabled);
        ApplyTheme(_current.Theme);
    }

    public FrontendPlatformServices PlatformServices { get; }

    public InterfaceMotionPolicy MotionPolicy { get; }

    public event EventHandler<UiPreferencesChangedEventArgs>? PreferencesChanged;

    public AppLanguage CurrentLanguage
    {
        get => _current.Language;
        set
        {
            if (value == _current.Language)
                return;

            var updated = _current with { Language = value };
            _store.WriteAsync(updated).GetAwaiter().GetResult();
            _current = updated;
            try
            {
                PreferencesChanged?.Invoke(
                    this,
                    new UiPreferencesChangedEventArgs(
                        true,
                        false,
                        false,
                        value,
                        _current.Theme));
            }
            finally
            {
                NotifyLanguagePersisted();
            }
        }
    }

    public AppThemeMode CurrentThemeMode
    {
        get => _current.Theme;
        set
        {
            if (value == _current.Theme)
                return;

            var updated = _current with { Theme = value };
            _store.WriteAsync(updated).GetAwaiter().GetResult();
            _current = updated;
            ApplyTheme(value);
            PreferencesChanged?.Invoke(
                this,
                new UiPreferencesChangedEventArgs(
                    false,
                    true,
                    false,
                    _current.Language,
                    value));
        }
    }


    public bool InterfaceAnimationsEnabled
    {
        get => _current.InterfaceAnimationsEnabled;
        set
        {
            if (value == _current.InterfaceAnimationsEnabled)
                return;

            var updated = _current with { InterfaceAnimationsEnabled = value };
            _store.WriteAsync(updated).GetAwaiter().GetResult();
            _current = updated;
            MotionPolicy.SetUserPreference(value);
            PreferencesChanged?.Invoke(
                this,
                new UiPreferencesChangedEventArgs(
                    false,
                    false,
                    true,
                    _current.Language,
                    _current.Theme));
        }
    }

    public string GetString(string key) => LocalizationManager.GetString(_current.Language, key);

    private void NotifyLanguagePersisted()
    {
        try
        {
            _changeNotifier?.NotifyLanguagePersistedAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"The persisted language change could not be signaled to the platform Agent: {exception.GetType().Name}");
        }
    }

    private static void ApplyTheme(AppThemeMode mode)
    {
        if (Application.Current is not Application app)
            return;

        app.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
