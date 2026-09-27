using PasswordManagerLocal.Common.Frontend.Abstractions.Services;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Auth;

public sealed class ChangeProfileViewModel : ViewModelBase
{
    private readonly IEndpoints _endpoints;
    private readonly IAuthSessionRegistry _authSessionRegistry;
    private readonly Func<Task> _navigateBackAsync;
    private readonly Action _navigateToLoginAnotherProfile;
    private readonly Func<Guid, Task> _selectProfileAsync;

    private bool _isBusy;
    private bool _isStartupSelection;

    public ChangeProfileViewModel(
        UiPreferencesService uiPreferences,
        IEndpoints endpoints,
        IAuthSessionRegistry authSessionRegistry,
        Func<Task> navigateBackAsync,
        Action navigateToLoginAnotherProfile,
        Func<Guid, Task> selectProfileAsync)
        : base(uiPreferences)
    {
        _endpoints = endpoints;
        _authSessionRegistry = authSessionRegistry;
        _navigateBackAsync = navigateBackAsync;
        _navigateToLoginAnotherProfile = navigateToLoginAnotherProfile;
        _selectProfileAsync = selectProfileAsync;

        BackCommand = Own(ReactiveCommand.CreateFromTask(_navigateBackAsync));
        RefreshCommand = Own(ReactiveCommand.CreateFromTask(LoadAsync));
        LoginAnotherProfileCommand = Own(ReactiveCommand.Create(_navigateToLoginAnotherProfile));
    }

    public ObservableCollection<ProfileSessionItemViewModel> Profiles { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool IsBackButtonVisible => !IsStartupSelection;

    public bool IsStartupSelection
    {
        get => _isStartupSelection;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isStartupSelection, value);
            this.RaisePropertyChanged(nameof(IsBackButtonVisible));
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(Subtitle));
            this.RaisePropertyChanged(nameof(LoggedInProfilesLabel));
            this.RaisePropertyChanged(nameof(EmptyProfilesTitle));
            this.RaisePropertyChanged(nameof(EmptyProfilesDescription));
        }
    }

    public bool HasProfiles => Profiles.Count > 0;

    public bool IsProfilesEmpty => Profiles.Count == 0;

    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> LoginAnotherProfileCommand { get; }

    public string Title => IsStartupSelection
        ? GetTranslation("Profiles_Startup_Title")
        : GetTranslation("Profiles_Title");

    public string Subtitle => IsStartupSelection
        ? GetTranslation("Profiles_Startup_Subtitle")
        : GetTranslation("Profiles_Subtitle");

    public string BackLabel => GetTranslation("Common_Back");

    public string RefreshLabel => GetTranslation("Common_Refresh");

    public string LoginAnotherProfileLabel => GetTranslation("Profiles_LoginAnother");

    public string LoggedInProfilesLabel => IsStartupSelection
        ? GetTranslation("Profiles_RememberedProfiles")
        : GetTranslation("Profiles_LoggedInProfiles");

    public string EmptyProfilesTitle => IsStartupSelection
        ? GetTranslation("Profiles_Startup_Empty_Title")
        : GetTranslation("Profiles_Empty_Title");

    public string EmptyProfilesDescription => IsStartupSelection
        ? GetTranslation("Profiles_Startup_Empty_Description")
        : GetTranslation("Profiles_Empty_Description");

    public string BusyText => GetTranslation("Common_Loading");

    protected override void OnLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Subtitle));
        this.RaisePropertyChanged(nameof(BackLabel));
        this.RaisePropertyChanged(nameof(IsBackButtonVisible));
        this.RaisePropertyChanged(nameof(RefreshLabel));
        this.RaisePropertyChanged(nameof(LoginAnotherProfileLabel));
        this.RaisePropertyChanged(nameof(LoggedInProfilesLabel));
        this.RaisePropertyChanged(nameof(EmptyProfilesTitle));
        this.RaisePropertyChanged(nameof(EmptyProfilesDescription));
        this.RaisePropertyChanged(nameof(BusyText));
    }

    public void SetStartupSelectionMode(bool isStartupSelection)
    {
        IsStartupSelection = isStartupSelection;
    }


    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        PrepareProfileListForLoading();
        try
        {
            foreach (var originalToken in _authSessionRegistry.ListTokens().ToList())
            {
                var profileItem = await CreateProfileSessionItemAsync(originalToken);
                if (profileItem is not null)
                    Profiles.Add(profileItem);
            }
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            IsBusy = false;
            RaiseProfilesChanged();
        }
    }

    private void PrepareProfileListForLoading()
    {
        ClearStatusMessage();
        IsBusy = true;
        foreach (var item in Profiles)
            item.Dispose();
        Profiles.Clear();
        RaiseProfilesChanged();
    }

    private async Task<ProfileSessionItemViewModel?> CreateProfileSessionItemAsync(Guid originalToken)
    {
        var token = await ResolveAuthenticatedTokenAsync(originalToken);
        if (token is null)
            return null;

        var profile = await _endpoints.GetUserProfileInfoAsync(token.Value);
        var displayName = BuildDisplayName(profile);
        var subtitle = string.IsNullOrWhiteSpace(profile.Username) ? profile.Email : $"@{profile.Username}";
        UpdateRegisteredProfile(token.Value, profile, displayName, subtitle);

        return new ProfileSessionItemViewModel(
            UiPreferences,
            token.Value,
            displayName,
            subtitle,
            token.Value == _authSessionRegistry.CurrentUserToken,
            SelectProfileAsync);
    }

    private async Task<Guid?> ResolveAuthenticatedTokenAsync(Guid originalToken)
    {
        var status = await _endpoints.GetAuthSessionStatusAsync(originalToken);
        if (status.IsAuthenticated)
            return originalToken;

        var restoredToken = await TryRestoreRememberedSessionAsync(originalToken);
        if (restoredToken is not null)
        {
            status = await _endpoints.GetAuthSessionStatusAsync(restoredToken.Value);
            if (status.IsAuthenticated)
                return restoredToken;
        }

        _authSessionRegistry.TryRemove(originalToken);
        return null;
    }

    private async Task<Guid?> TryRestoreRememberedSessionAsync(Guid originalToken)
    {
        var session = _authSessionRegistry.GetSession(originalToken);
        if (session?.IsRememberMeEnabled != true || session.UserId == Guid.Empty)
            return null;

        try
        {
            var newToken = await _endpoints.InitializeRememberMeSessionAsync(session.UserId);
            if (!_authSessionRegistry.TryReplaceToken(originalToken, newToken))
            {
                _authSessionRegistry.TryAdd(newToken, originalToken == _authSessionRegistry.CurrentUserToken);
                _authSessionRegistry.TryRemove(originalToken);
            }

            return newToken;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRegisteredProfile(
        Guid token,
        UserProfileInfoResponse profile,
        string displayName,
        string subtitle)
    {
        _authSessionRegistry.TrySetProfile(
            token,
            profile.UId,
            displayName,
            subtitle,
            profile.Username,
            profile.Email,
            profile.IsRememberMeEnabled);
    }

    public void RefreshCurrentSelection()
    {
        var current = _authSessionRegistry.CurrentUserToken;
        foreach (var profile in Profiles)
            profile.IsCurrent = profile.Token == current;
    }

    private async Task SelectProfileAsync(Guid token)
    {
        if (IsBusy || token == Guid.Empty)
            return;

        try
        {
            IsBusy = true;
            ClearStatusMessage();
            await _selectProfileAsync(token);
            RefreshCurrentSelection();
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseProfilesChanged()
    {
        this.RaisePropertyChanged(nameof(HasProfiles));
        this.RaisePropertyChanged(nameof(IsProfilesEmpty));
    }

    private static string BuildDisplayName(UserProfileInfoResponse profile)
    {
        var fullName = string.Join(
            " ",
            new[] { profile.LastName?.Trim(), profile.FirstName?.Trim() }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;

        if (!string.IsNullOrWhiteSpace(profile.Username))
            return profile.Username;

        return profile.Email;
    }
    protected override void DisposeManaged()
    {
        foreach (var profile in Profiles)
            profile.Dispose();
        Profiles.Clear();
        base.DisposeManaged();
    }

}
