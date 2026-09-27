using PasswordManagerLocal.Common.Frontend.Localization;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class SettingsLanguageOptionViewModel : ReactiveObject
{
    private string _displayName;

    public SettingsLanguageOptionViewModel(AppLanguage language, string displayName)
    {
        Language = language;
        _displayName = displayName;
    }

    public AppLanguage Language { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    public void UpdateDisplayName(string displayName)
    {
        if (!string.Equals(_displayName, displayName, StringComparison.Ordinal))
            DisplayName = displayName;
    }
}
