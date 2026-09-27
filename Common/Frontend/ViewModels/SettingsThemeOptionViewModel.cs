using PasswordManagerLocal.Common.Frontend.Localization;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class SettingsThemeOptionViewModel : ReactiveObject
{
    private string _displayName;

    public SettingsThemeOptionViewModel(AppThemeMode theme, string displayName)
    {
        Theme = theme;
        _displayName = displayName;
    }

    public AppThemeMode Theme { get; }

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
