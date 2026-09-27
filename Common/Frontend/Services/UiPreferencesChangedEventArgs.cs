using PasswordManagerLocal.Common.Frontend.Localization;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class UiPreferencesChangedEventArgs : EventArgs
{
    public UiPreferencesChangedEventArgs(bool languageChanged, bool themeChanged, AppLanguage language, AppThemeMode theme)
    {
        LanguageChanged = languageChanged;
        ThemeChanged = themeChanged;
        Language = language;
        Theme = theme;
    }

    public bool LanguageChanged { get; }

    public bool ThemeChanged { get; }

    public AppLanguage Language { get; }

    public AppThemeMode Theme { get; }
}
