using PasswordManagerLocal.Common.Frontend.Localization;
using ReactiveUI;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class SettingsThemeOptionViewModel : ReactiveObject
{
    private string _displayName;

    public SettingsThemeOptionViewModel(
        AppThemeId theme,
        string displayNameResourceKey,
        string displayName)
    {
        Theme = theme;
        DisplayNameResourceKey = displayNameResourceKey;
        _displayName = displayName;
    }

    public AppThemeId Theme { get; }

    public string DisplayNameResourceKey { get; }

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
