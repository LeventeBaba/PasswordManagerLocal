using PasswordManagerLocal.Common.Frontend.Localization;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class UiPreferencesChangedEventArgs : EventArgs
{
    public UiPreferencesChangedEventArgs(
        bool languageChanged,
        bool themeChanged,
        AppLanguage language,
        AppThemeMode theme)
        : this(languageChanged, themeChanged, false, language, theme)
    {
    }

    public UiPreferencesChangedEventArgs(
        bool languageChanged,
        bool themeChanged,
        bool interfaceAnimationsChanged,
        AppLanguage language,
        AppThemeMode theme)
    {
        LanguageChanged = languageChanged;
        ThemeChanged = themeChanged;
        InterfaceAnimationsChanged = interfaceAnimationsChanged;
        Language = language;
        Theme = theme;
    }

    public bool LanguageChanged { get; }

    public bool ThemeChanged { get; }

    public bool InterfaceAnimationsChanged { get; }

    public AppLanguage Language { get; }

    public AppThemeMode Theme { get; }
}
