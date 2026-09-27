using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Auth;

public sealed class ProfileSessionItemViewModel : ViewModelBase
{
    private bool _isCurrent;

    public ProfileSessionItemViewModel(
        UiPreferencesService uiPreferences,
        Guid token,
        string displayName,
        string subtitle,
        bool isCurrent,
        Func<Guid, Task> selectAsync)
        : base(uiPreferences)
    {
        Token = token;
        DisplayName = displayName;
        Subtitle = subtitle;
        _isCurrent = isCurrent;
        SelectCommand = Own(ReactiveCommand.CreateFromTask(() => selectAsync(Token)));
    }

    public Guid Token { get; }

    public string DisplayName { get; }

    public string Subtitle { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCurrent, value);
            this.RaisePropertyChanged(nameof(ActionLabel));
            this.RaisePropertyChanged(nameof(CurrentBadgeLabel));
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> SelectCommand { get; }

    public string ActionLabel => IsCurrent
        ? GetTranslation("Profiles_Current")
        : GetTranslation("Profiles_SwitchTo");

    public string CurrentBadgeLabel => IsCurrent
        ? GetTranslation("Profiles_Current")
        : string.Empty;

    protected override void OnLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(ActionLabel));
        this.RaisePropertyChanged(nameof(CurrentBadgeLabel));
    }
}
