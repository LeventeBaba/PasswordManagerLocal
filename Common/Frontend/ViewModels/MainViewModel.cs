using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.Abstractions.Services;
using PasswordManagerLocal.Common.Frontend.Exceptions;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Devices;
using PasswordManagerLocal.Common.Contracts.Authentication;
using PasswordManagerLocal.Common.Contracts.Security;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IFrontendRefreshTarget
{
    private static readonly TimeSpan SessionRenewalWarningLeadTime = TimeSpan.FromMinutes(1);
    private readonly IEndpoints _endpoints;
    private readonly IBackendRuntimeClient _backendClient;
    private readonly IAuthSessionRegistry _authSessionRegistry;
    private readonly DeviceAppPreferencesService _deviceAppPreferences;
    private readonly object _initializationGate = new();
    private Task? _initializationTask;

    private ViewModelBase _currentPageViewModel;
    private MainPageContentViewModel _currentPageContentViewModel;
    private bool _isPageTransitionReversed;
    private bool _isAuthenticated;
    private bool _isBackendInitialized;
    private string _currentUserDisplayName = string.Empty;
    private string _currentUserSubtitle = string.Empty;
    private readonly IFrontendDeadlineScheduler _deadlineScheduler;
    private readonly Dictionary<Guid, IDisposable> _sessionDeadlineTimers = new();
    private readonly Dictionary<Guid, DateTimeOffset> _failedRememberedRenewalExpiries = new();
    private bool _isSessionRenewalDialogOpen;
    private bool _isRenewingSession;
    private bool _isAddingProfile;
    private bool _isStartupProfileSelection;
    private bool _isCurrentRememberMeEnabled;
    private bool _isApplyingRememberMeFromSession;
    private bool _isSettingRememberMe;
    private int _profileChangeReturnPageIndex;
    private Guid _sessionRenewalDialogToken = Guid.Empty;
    private Guid _sessionRenewalPromptShownForToken = Guid.Empty;
    private string _sessionRenewalDialogProfileName = string.Empty;
    private readonly HashSet<Guid> _autoRenewingSessionTokens = new();
    private DatabaseRecoveryStage _databaseRecoveryStage;
    private DatabaseVersionNotSupportedException? _databaseVersionException;
    private bool _isResettingDatabase;
    private RegistrationViewModel? _registrationViewModel;
    private PasswordsViewModel? _passwordsViewModel;
    private ProfileViewModel? _profileViewModel;
    private ChangeProfileViewModel? _changeProfileViewModel;
    private ViewModelBase? _observedPageStatusViewModel;
    private ViewModelBase? _settingsReturnPageViewModel;
    private readonly HelpCatalog _helpCatalog = HelpCatalogLoader.Load();
    private readonly ThirdPartyNoticeCatalog _noticeCatalog;
    private readonly InformationNavigationHistory _informationNavigation = new();
    private readonly Dictionary<InformationRoute, HelpPageViewModel> _helpPages = new();
    private IReadOnlyList<MobileMainPageViewModel>? _mobileMainPages;
    private int _currentMobileNavigationIndex;

    public MainViewModel(
        IEndpoints endpoints,
        IBackendRuntimeClient backendClient,
        IBackgroundSyncSettingsClient backgroundSyncSettingsClient,
        UiPreferencesService uiPreferences,
        IAuthSessionRegistry? authSessionRegistry = null,
        CancellationToken sessionLifetime = default,
        IFrontendDeadlineScheduler? deadlineScheduler = null,
        string? applicationVersion = null)
        : this(
            endpoints,
            backendClient,
            authSessionRegistry ?? new AuthSessionRegistry(),
            uiPreferences,
            new DeviceAppPreferencesService(backgroundSyncSettingsClient, sessionLifetime),
            deadlineScheduler ?? new AvaloniaFrontendDeadlineScheduler(),
            applicationVersion ?? ApplicationVersionInfo.GetDisplayVersion())
    {
    }

    private MainViewModel(
        IEndpoints endpoints,
        IBackendRuntimeClient backendClient,
        IAuthSessionRegistry authSessionRegistry,
        UiPreferencesService uiPreferences,
        DeviceAppPreferencesService deviceAppPreferences,
        IFrontendDeadlineScheduler deadlineScheduler,
        string? applicationVersion)
        : base(uiPreferences)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _backendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
        _authSessionRegistry = authSessionRegistry;
        _deviceAppPreferences = deviceAppPreferences ?? throw new ArgumentNullException(nameof(deviceAppPreferences));
        _deadlineScheduler = deadlineScheduler ?? throw new ArgumentNullException(nameof(deadlineScheduler));

        LoginViewModel = new LoginViewModel(
            uiPreferences,
            _endpoints,
            NavigateToRegistration,
            OnAuthenticationSucceededAsync,
            _deadlineScheduler);
        LoginViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;

        _currentPageViewModel = LoginViewModel;
        _currentPageContentViewModel = new MainPageContentViewModel(LoginViewModel);
        ObservePageStatus(_currentPageViewModel);

        var noticeCatalog = ThirdPartyNoticeCatalogLoader.Load();
        _noticeCatalog = noticeCatalog;
        AboutViewModel = new AboutViewModel(
            uiPreferences,
            applicationVersion,
            NavigateBackFromAbout,
            NavigateToThirdPartyLicenses,
            noticeCatalog.Notices.Count != 0);
        ThirdPartyLicensesViewModel = new ThirdPartyLicensesViewModel(
            uiPreferences,
            noticeCatalog,
            NavigateBackFromThirdPartyLicenses,
            NavigateToThirdPartyLicenseDetail);
        ThirdPartyLicenseDetailViewModel = new ThirdPartyLicenseDetailViewModel(
            uiPreferences,
            NavigateBackFromThirdPartyLicenseDetail);

        SettingsViewModel = new SettingsViewModel(
            uiPreferences,
            _deviceAppPreferences,
            NavigateBackFromSettings,
            NavigateToAbout,
            NavigateToHelp);
        SettingsViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;

        ShowSettingsCommand = Own(ReactiveCommand.Create(NavigateToSettings));
        ShowPasswordsCommand = Own(ReactiveCommand.Create(NavigateToPasswords));
        ShowProfileCommand = Own(ReactiveCommand.Create(NavigateToProfile));
        ShowDevicesCommand = Own(ReactiveCommand.Create(NavigateToDevices));
        SelectMobilePasswordsCommand = Own(ReactiveCommand.Create(() => NavigateToMainPageFromMobileDropdown(0)));
        SelectMobileDevicesCommand = Own(ReactiveCommand.Create(() => NavigateToMainPageFromMobileDropdown(1)));
        SelectMobileProfileCommand = Own(ReactiveCommand.Create(() => NavigateToMainPageFromMobileDropdown(2)));
        ShowLoginCommand = Own(ReactiveCommand.Create(NavigateToLogin));
        ShowRegistrationCommand = Own(ReactiveCommand.Create(NavigateToRegistration));
        ShowChangeProfileCommand = Own(ReactiveCommand.CreateFromTask(ShowChangeProfileAsync));
        LogoutCommand = Own(ReactiveCommand.CreateFromTask(LogoutAsync));
        ConfirmSessionRenewalCommand = Own(ReactiveCommand.CreateFromTask(ConfirmSessionRenewalAsync));
        DeclineSessionRenewalCommand = Own(ReactiveCommand.Create(DeclineSessionRenewal));
        DatabaseRecoveryPrimaryCommand = Own(ReactiveCommand.CreateFromTask(HandleDatabaseRecoveryPrimaryActionAsync));
        DatabaseRecoverySecondaryCommand = Own(ReactiveCommand.Create(HandleDatabaseRecoverySecondaryAction));
        _backendClient.StateChanged += HandleBackendRuntimeStateChanged;
        ApplyBackendRuntimeSnapshot(_backendClient.Snapshot);
        PlatformServices.SensitiveData.HideVisibleSecretsRequested += HandleHideVisibleSecretsRequested;
    }

    protected override void DisposeManaged()
    {
        _backendClient.StateChanged -= HandleBackendRuntimeStateChanged;
        PlatformServices.SensitiveData.HideVisibleSecretsRequested -= HandleHideVisibleSecretsRequested;
        StopSessionDeadlineTimers();
        StopObservingPageStatus();
        _settingsReturnPageViewModel = null;
        LoginViewModel.Dispose();
        SettingsViewModel.Dispose();
        AboutViewModel.Dispose();
        ThirdPartyLicensesViewModel.Dispose();
        ThirdPartyLicenseDetailViewModel.Dispose();
        foreach (var page in _helpPages.Values)
            page.Dispose();
        _helpPages.Clear();
        _informationNavigation.Clear();
        _registrationViewModel?.Dispose();
        _passwordsViewModel?.Dispose();
        _profileViewModel?.Dispose();
        _changeProfileViewModel?.Dispose();
        LoginViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        SettingsViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        if (_registrationViewModel is not null)
            _registrationViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        if (_passwordsViewModel is not null)
            _passwordsViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        if (_profileViewModel is not null)
            _profileViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        if (_changeProfileViewModel is not null)
            _changeProfileViewModel.PropertyChanged -= HandleRefreshPageViewModelPropertyChanged;
        _registrationViewModel = null;
        _passwordsViewModel = null;
        _profileViewModel = null;
        _changeProfileViewModel = null;
        _autoRenewingSessionTokens.Clear();
        _sessionRenewalDialogToken = Guid.Empty;
        _sessionRenewalPromptShownForToken = Guid.Empty;
        _sessionRenewalDialogProfileName = string.Empty;
        CurrentUserDisplayName = string.Empty;
        CurrentUserSubtitle = string.Empty;
        IsAuthenticated = false;
        _currentPageViewModel = LoginViewModel;
        _currentPageContentViewModel = new MainPageContentViewModel(LoginViewModel);
        base.DisposeManaged();
    }

    public LoginViewModel LoginViewModel { get; }

    public event EventHandler? RefreshNavigationChanged;
    public event EventHandler? RefreshPageStateChanged;
    public event EventHandler? RefreshAuthenticationChanged;

    public SettingsViewModel SettingsViewModel { get; }

    public AboutViewModel AboutViewModel { get; }

    public ThirdPartyLicensesViewModel ThirdPartyLicensesViewModel { get; }

    public ThirdPartyLicenseDetailViewModel ThirdPartyLicenseDetailViewModel { get; }

    public RegistrationViewModel RegistrationViewModel
    {
        get
        {
            if (_registrationViewModel is null)
            {
                _registrationViewModel = new RegistrationViewModel(
                    UiPreferences,
                    _endpoints,
                    NavigateToLogin,
                    OnAuthenticationSucceededAsync);
                _registrationViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;
                _registrationViewModel.SetBackendInitialized(_isBackendInitialized);
            }

            return _registrationViewModel;
        }
    }

    public PasswordsViewModel PasswordsViewModel
    {
        get
        {
            if (_passwordsViewModel is null)
            {
                _passwordsViewModel = new PasswordsViewModel(UiPreferences, _endpoints, _authSessionRegistry);
                _passwordsViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;
            }
            return _passwordsViewModel;
        }
    }

    public ProfileViewModel ProfileViewModel
    {
        get
        {
            if (_profileViewModel is null)
            {
                _profileViewModel = new ProfileViewModel(
                    UiPreferences,
                    _endpoints,
                    RefreshProfileDataAsync,
                    HandleAccountDeletedAsync);
                _profileViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;
            }
            return _profileViewModel;
        }
    }

    public ChangeProfileViewModel ChangeProfileViewModel
    {
        get
        {
            if (_changeProfileViewModel is null)
            {
                _changeProfileViewModel = new ChangeProfileViewModel(
                    UiPreferences,
                    _endpoints,
                    _authSessionRegistry,
                    NavigateBackFromChangeProfileAsync,
                    NavigateToLoginAnotherProfile,
                    SwitchToProfileAsync,
                    SetSessionExpiry);
                _changeProfileViewModel.PropertyChanged += HandleRefreshPageViewModelPropertyChanged;
            }
            return _changeProfileViewModel;
        }
    }

    public ViewModelBase CurrentPageViewModel
    {
        get => _currentPageViewModel;
        private set
        {
            if (IsDisposed || ReferenceEquals(_currentPageViewModel, value))
            {
                return;
            }

            // A documentation visit suspends the current task. In particular, leaving
            // Login normally cancels enrollment; opening Settings/help must not do that.
            if (!ReferenceEquals(value, SettingsViewModel) && !IsInformationViewModel(value))
                _currentPageViewModel.OnNavigatedFrom();
            StopObservingPageStatus();
            ClearStatusMessage();
            this.RaiseAndSetIfChanged(ref _currentPageViewModel, value);
            ObservePageStatus(value);
            this.RaisePropertyChanged(nameof(CurrentDesktopPageViewModel));
            CurrentPageContentViewModel = new MainPageContentViewModel(value);
            RaiseNavigationStateProperties();
            RaiseHeaderSubtitleProperties();
            RefreshNavigationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            var changed = _isAuthenticated != value;
            this.RaiseAndSetIfChanged(ref _isAuthenticated, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(CurrentUserId));
                RefreshAuthenticationChanged?.Invoke(this, EventArgs.Empty);
            }
            this.RaisePropertyChanged(nameof(IsAnonymous));
            RaiseHeaderSubtitleProperties();
            this.RaisePropertyChanged(nameof(IsDesktopContentVisible));
            this.RaisePropertyChanged(nameof(IsDesktopNavigationVisible));
            this.RaisePropertyChanged(nameof(IsMobilePageIndicatorVisible));
            this.RaisePropertyChanged(nameof(IsPasswordsMainPageSelected));
            this.RaisePropertyChanged(nameof(IsDevicesMainPageSelected));
            this.RaisePropertyChanged(nameof(IsProfileMainPageSelected));
            this.RaisePropertyChanged(nameof(CanChangeRememberMe));
            this.RaisePropertyChanged(nameof(IsMobileMainContentVisible));
            this.RaisePropertyChanged(nameof(IsMobileStandaloneContentVisible));
            this.RaisePropertyChanged(nameof(CurrentMobileStandalonePageViewModel));
            this.RaisePropertyChanged(nameof(MobileMainPages));
            RaiseMobileSwipeEligibilityProperties();
        }
    }

    public bool IsAnonymous => !IsAuthenticated;

    /// <summary>The selected authenticated profile's stable user identity, if available.</summary>
    public Guid? CurrentUserId
    {
        get
        {
            if (!IsAuthenticated)
                return null;

            var userId = _authSessionRegistry.GetSession(_authSessionRegistry.CurrentUserToken)?.UserId;
            return userId is { } value && value != Guid.Empty ? value : null;
        }
    }

    public bool IsSessionRenewalDialogOpen
    {
        get => _isSessionRenewalDialogOpen;
        private set
        {
            if (_isSessionRenewalDialogOpen == value)
                return;

            this.RaiseAndSetIfChanged(ref _isSessionRenewalDialogOpen, value);
            RaiseMobileSwipeEligibilityProperties();
        }
    }

    public bool IsRenewingSession
    {
        get => _isRenewingSession;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRenewingSession, value);
            this.RaisePropertyChanged(nameof(SessionRenewalYesButtonLabel));
        }
    }

    public MainPageContentViewModel CurrentPageContentViewModel
    {
        get => _currentPageContentViewModel;
        private set
        {
            if (ReferenceEquals(_currentPageContentViewModel, value))
                return;

            this.RaiseAndSetIfChanged(ref _currentPageContentViewModel, value);
            this.RaisePropertyChanged(nameof(CurrentMobileStandalonePageViewModel));
        }
    }

    public MainPageContentViewModel? CurrentMobileStandalonePageViewModel =>
        IsMobileStandaloneContentVisible ? CurrentPageContentViewModel : null;

    public bool IsPageTransitionReversed
    {
        get => _isPageTransitionReversed;
        private set => this.RaiseAndSetIfChanged(ref _isPageTransitionReversed, value);
    }

    public bool IsMobileNavigationEnabled => OperatingSystem.IsAndroid();

    public bool IsDesktopContentVisible => !IsMobileNavigationEnabled;

    public ViewModelBase? CurrentDesktopPageViewModel => IsAndroidUi ? null : CurrentPageViewModel;

    public bool IsMobileMainContentVisible => IsMobileNavigationEnabled && IsMainContentPageVisible;

    public bool IsMobileStandaloneContentVisible => IsMobileNavigationEnabled && !IsMainContentPageVisible;

    public bool IsDesktopNavigationVisible => IsAuthenticated && !IsMobileNavigationEnabled && IsMainContentPageVisible;

    public bool IsMobilePageIndicatorVisible => IsAuthenticated && IsMobileNavigationEnabled && IsMainContentPageVisible;

    public bool IsMobileMainPageAnimationEnabled =>
        EffectiveInterfaceAnimationsEnabled;

    public bool CanNavigateMobileMainPagesBySwipe =>
        IsMobileNavigationEnabled &&
        IsAuthenticated &&
        IsMainContentPageVisible &&
        IsApplicationInteractionEnabled &&
        !HasConfirmableDialogOpen &&
        IsCurrentMainPageAtSwipeRoot();

    public bool IsAnimatedMobileMainPageSwipeEnabled =>
        IsMobileMainPageAnimationEnabled && CanNavigateMobileMainPagesBySwipe;

    public bool IsCustomMobileMainPageSwipeEnabled =>
        !IsMobileMainPageAnimationEnabled && CanNavigateMobileMainPagesBySwipe;

    public bool IsPasswordsMainPageSelected => IsAuthenticated && CurrentMainPageIndex == 0;

    public bool IsDevicesMainPageSelected => IsAuthenticated && CurrentMainPageIndex == 1;

    public bool IsProfileMainPageSelected => IsAuthenticated && CurrentMainPageIndex == 2;

    public bool IsSettingsPageVisible => ReferenceEquals(CurrentPageViewModel, SettingsViewModel);

    public bool IsInformationPageVisible =>
        IsInformationViewModel(CurrentPageViewModel);

    public bool IsSettingsSectionVisible => IsSettingsPageVisible || IsInformationPageVisible;

    private bool IsMainContentPageVisible =>
        ReferenceEquals(CurrentPageViewModel, _passwordsViewModel) ||
        ReferenceEquals(CurrentPageViewModel, _profileViewModel);

    public IReadOnlyList<MobileMainPageViewModel> MobileMainPages
    {
        get
        {
            if (!IsMobileNavigationEnabled || !IsAuthenticated)
                return Array.Empty<MobileMainPageViewModel>();

            return _mobileMainPages ??=
            [
                new MobilePasswordsMainPageViewModel(PasswordsViewModel),
                new MobileDevicesMainPageViewModel(ProfileViewModel),
                new MobileProfileMainPageViewModel(ProfileViewModel)
            ];
        }
    }

    public int CurrentMobileNavigationIndex
    {
        get => _currentMobileNavigationIndex;
        set
        {
            if (value is < 0 or > 2 || value == _currentMobileNavigationIndex)
                return;

            if (!CanNavigateMobileMainPagesBySwipe)
            {
                this.RaisePropertyChanged(nameof(CurrentMobileNavigationIndex));
                return;
            }

            NavigateToMainPageFromMobileCarousel(value);
        }
    }

    public string MobileCurrentPageLabel => _currentMobileNavigationIndex switch
    {
        1 => DevicesLabel,
        2 => ProfileLabel,
        _ => PasswordVaultLabel
    };

    public string MobilePageIndicatorText => _currentMobileNavigationIndex switch
    {
        1 => "○ ● ○",
        2 => "○ ○ ●",
        _ => "● ○ ○"
    };


    public string CurrentUserDisplayName
    {
        get => _currentUserDisplayName;
        private set => this.RaiseAndSetIfChanged(ref _currentUserDisplayName, value);
    }

    public string CurrentUserSubtitle
    {
        get => _currentUserSubtitle;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentUserSubtitle, value);
            RaiseHeaderSubtitleProperties();
        }
    }

    public bool IsCurrentRememberMeEnabled
    {
        get => _isCurrentRememberMeEnabled;
        set
        {
            if (_isCurrentRememberMeEnabled == value)
                return;

            var previousValue = _isCurrentRememberMeEnabled;
            this.RaiseAndSetIfChanged(ref _isCurrentRememberMeEnabled, value);

            if (!_isApplyingRememberMeFromSession)
                _ = SetCurrentRememberMeAsync(value, previousValue);
        }
    }

    public bool IsSettingRememberMe
    {
        get => _isSettingRememberMe;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSettingRememberMe, value);
            this.RaisePropertyChanged(nameof(CanChangeRememberMe));
        }
    }

    public bool CanChangeRememberMe => IsAuthenticated && !IsSettingRememberMe;

    public ReactiveCommand<RxVoid, RxVoid> ShowSettingsCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowPasswordsCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowDevicesCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SelectMobilePasswordsCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SelectMobileDevicesCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SelectMobileProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowLoginCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowRegistrationCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowChangeProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> LogoutCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmSessionRenewalCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeclineSessionRenewalCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DatabaseRecoveryPrimaryCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DatabaseRecoverySecondaryCommand { get; }

    public bool IsDatabaseRecoveryDialogOpen => _databaseRecoveryStage != DatabaseRecoveryStage.None;

    public bool IsApplicationInteractionEnabled => !IsDatabaseRecoveryDialogOpen;

    public bool IsDatabaseResetInProgress => _isResettingDatabase;

    public bool IsDatabaseRecoverySecondaryButtonVisible =>
        !_isResettingDatabase && _databaseRecoveryStage != DatabaseRecoveryStage.Declined;

    public bool IsDatabaseRecoveryPrimaryButtonVisible => !_isResettingDatabase;

    public string DatabaseRecoveryTitle => _databaseRecoveryStage switch
    {
        DatabaseRecoveryStage.FinalConfirmation => GetTranslation("DatabaseVersion_ResetConfirm_Title"),
        DatabaseRecoveryStage.Declined => GetTranslation("DatabaseVersion_Blocked_Title"),
        DatabaseRecoveryStage.ResetFailed => GetTranslation("DatabaseVersion_ResetFailed_Title"),
        _ => GetTranslation("DatabaseVersion_Error_Title")
    };

    public string DatabaseRecoveryMessage
    {
        get
        {
            if (_isResettingDatabase)
                return GetTranslation("DatabaseVersion_Resetting_Message");

            return _databaseRecoveryStage switch
            {
                DatabaseRecoveryStage.FinalConfirmation => GetTranslation("DatabaseVersion_ResetConfirm_Message"),
                DatabaseRecoveryStage.Declined => GetTranslation("DatabaseVersion_ResetDeclined_Message"),
                DatabaseRecoveryStage.ResetFailed => GetTranslation("DatabaseVersion_ResetFailed_Message"),
                _ => BuildDatabaseVersionErrorMessage()
            };
        }
    }

    public string DatabaseRecoveryPrimaryButtonLabel =>
        _databaseRecoveryStage == DatabaseRecoveryStage.FinalConfirmation
            ? GetTranslation("DatabaseVersion_ResetConfirm_Button")
            : GetTranslation("DatabaseVersion_Reset_Button");

    public string DatabaseRecoverySecondaryButtonLabel =>
        _databaseRecoveryStage == DatabaseRecoveryStage.FinalConfirmation ||
        _databaseRecoveryStage == DatabaseRecoveryStage.ResetFailed
            ? GetTranslation("Common_Back")
            : GetTranslation("DatabaseVersion_DoNotReset_Button");

    public string AppTitle => GetTranslation("AppTitle");

    public string SettingsLabel => GetTranslation("Settings");

    public string PasswordVaultLabel => GetTranslation("Shell_PasswordVault");

    public string ProfileLabel => GetTranslation("Shell_Profile");

    public string DevicesLabel => GetTranslation("Shell_Devices");

    public string LogoutLabel => GetTranslation("Shell_Logout");

    public string ChangeProfileLabel => GetTranslation("Shell_ChangeProfile");

    public string RememberMeDropdownLabel => GetTranslation("Shell_RememberMeSwitch");

    public string RememberMeWarningLabel => GetTranslation("Help_Warning");

    public string RememberMeWarningText => GetTranslation("Security_RememberMe_Warning");

    public string RememberMeOnLabel => GetTranslation("Common_On");

    public string RememberMeOffLabel => GetTranslation("Common_Off");

    public string WelcomeLabel => GetTranslation("Shell_Welcome");

    public string NavigationLabel => GetTranslation("Shell_Navigation");

    public string YesLabel => GetTranslation("Common_Yes");

    public string NoLabel => GetTranslation("Common_No");

    public string LogoutConfirmationTitle => GetTranslation("Shell_LogoutConfirm_Title");

    public string LogoutConfirmationMessage => GetTranslation("Shell_LogoutConfirm_Message");

    public string ExitConfirmationTitle => GetTranslation("Shell_ExitConfirm_Title");

    public string ExitConfirmationMessage => GetTranslation("Shell_ExitConfirm_Message");

    public string SessionRenewalWarningTitle => GetTranslation("Shell_SessionRenewal_Title");

    public string SessionRenewalWarningMessage => string.IsNullOrWhiteSpace(_sessionRenewalDialogProfileName)
        ? GetTranslation("Shell_SessionRenewal_Message")
        : string.Format(GetTranslation("Shell_SessionRenewal_ProfileMessage"), _sessionRenewalDialogProfileName);

    public string SessionRenewalYesButtonLabel => IsRenewingSession
        ? GetTranslation("Common_Loading")
        : YesLabel;

    public string HeaderSubtitle
    {
        get
        {
            if (TryGetShellHeaderStatus(out var shellMessage, out _))
                return shellMessage;

            if (TryGetCurrentPageHeaderStatus(out var pageMessage, out _))
                return pageMessage;

            return IsAuthenticated ? CurrentUserSubtitle : string.Empty;
        }
    }

    public bool HasHeaderSubtitle => !string.IsNullOrWhiteSpace(HeaderSubtitle);

    public bool IsHeaderSubtitleUser => HasHeaderSubtitle && HeaderSubtitleKind == OperationMessageKind.None;

    public bool IsHeaderSubtitleInformation => HeaderSubtitleKind == OperationMessageKind.Information;

    public bool IsHeaderSubtitleSuccess => HeaderSubtitleKind == OperationMessageKind.Success;

    public bool IsHeaderSubtitleError => HeaderSubtitleKind == OperationMessageKind.Error;

    public bool HasNonErrorHeaderSubtitle => HasHeaderSubtitle && !IsHeaderSubtitleError;

    public Task InitializeAsync()
    {
        if (IsDisposed || LifetimeToken.IsCancellationRequested)
            return Task.CompletedTask;
        Task operation;
        TaskCompletionSource? starter = null;

        lock (_initializationGate)
        {
            if (IsAuthenticated)
                return Task.CompletedTask;

            if (_initializationTask is not null)
            {
                operation = _initializationTask;
            }
            else
            {
                starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _initializationTask = starter.Task;
                operation = starter.Task;
            }
        }

        if (starter is not null)
            _ = RunInitializationAsync(starter);

        return operation;
    }

    private async Task RunInitializationAsync(TaskCompletionSource completion)
    {
        try
        {
            await InitializeCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_initializationGate)
            {
                if (ReferenceEquals(_initializationTask, completion.Task))
                    _initializationTask = null;
            }
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (IsAuthenticated)
            return;

        ShowLocalizedInformationMessage("Shell_BackendStarting");

        try
        {
            await _backendClient.ConnectAsync(LifetimeToken);
            await _backendClient.WaitUntilReadyAsync(LifetimeToken);
            LifetimeToken.ThrowIfCancellationRequested();
            var snapshot = _backendClient.Snapshot;
            ApplyBackendAvailability(snapshot.IsReady);
            if (!snapshot.IsReady)
                return;
        }
        catch (DatabaseVersionNotSupportedException exception)
        {
            if (!IsAuthenticated)
                ShowDatabaseRecoveryDialog(exception);
            return;
        }
        catch (KeyProtectorUnavailableException)
        {
            if (!IsAuthenticated)
                ShowLocalizedErrorMessage("Shell_DeviceUnlockRequired");
            return;
        }
        catch
        {
            if (!IsAuthenticated)
                ShowLocalizedErrorMessage("Shell_BackendStartupFailed");
            return;
        }

        IReadOnlyList<Guid> rememberedTokens;
        try
        {
            rememberedTokens = await _endpoints.RestoreRememberedSessionsAsync();
        }
        catch (KeyProtectorUnavailableException)
        {
            if (!IsAuthenticated)
                ShowLocalizedErrorMessage("Shell_DeviceUnlockRequired");
            return;
        }
        catch
        {
            if (!IsAuthenticated)
                ShowLocalizedErrorMessage("Shell_RememberedSessionRestoreFailed");
            return;
        }

        var loadedRememberedTokens = new List<Guid>();

        foreach (var token in rememberedTokens)
        {
            if (await TryAddRememberedSessionAsync(token, false))
                loadedRememberedTokens.Add(token);
        }

        if (loadedRememberedTokens.Count == 1)
        {
            await LoadAuthenticatedStateAsync(
                loadedRememberedTokens[0],
                () => GetTranslation("Shell_RememberedSessionLoaded"));
            return;
        }

        if (loadedRememberedTokens.Count > 1)
        {
            await ShowStartupProfileSelectionAsync(
                () => GetTranslation("Shell_ChooseRememberedProfile"));
            return;
        }

        if (!IsAuthenticated)
            ClearStatusMessage();
    }

    internal bool HasConfirmableDialogOpen
    {
        get
        {
            if (IsSessionRenewalDialogOpen
                || (IsDatabaseRecoveryDialogOpen && IsDatabaseRecoveryPrimaryButtonVisible))
            {
                return true;
            }

            if (_passwordsViewModel is not null
                && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel)
                && _passwordsViewModel.HasConfirmableDialogOpen)
            {
                return true;
            }

            return _profileViewModel is not null
                && ReferenceEquals(CurrentPageViewModel, _profileViewModel)
                && _profileViewModel.HasConfirmableDialogOpen;
        }
    }

    internal async Task ConfirmOpenDialogAsync()
    {
        if (IsSessionRenewalDialogOpen)
        {
            await ConfirmSessionRenewalAsync();
            return;
        }

        if (IsDatabaseRecoveryDialogOpen && IsDatabaseRecoveryPrimaryButtonVisible)
        {
            await HandleDatabaseRecoveryPrimaryActionAsync();
            return;
        }

        if (_passwordsViewModel is not null
            && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel)
            && _passwordsViewModel.HasConfirmableDialogOpen)
        {
            await _passwordsViewModel.ConfirmOpenDialogAsync();
            return;
        }

        if (_profileViewModel is not null
            && ReferenceEquals(CurrentPageViewModel, _profileViewModel)
            && _profileViewModel.HasConfirmableDialogOpen)
        {
            await _profileViewModel.ConfirmOpenDialogAsync();
        }
    }


    public async Task<bool> TryNavigateBackAsync()
    {
        if (IsSessionRenewalDialogOpen)
        {
            DeclineSessionRenewal();
            return true;
        }

        if (IsDatabaseRecoveryDialogOpen)
        {
            if (!_isResettingDatabase)
                HandleDatabaseRecoverySecondaryAction();

            return true;
        }

        if (IsInformationPageVisible)
        {
            NavigateBackFromInformation();
            return true;
        }

        if (IsSettingsPageVisible)
        {
            NavigateBackFromSettings();
            return true;
        }

        if (_passwordsViewModel is not null && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel))
        {
            return _passwordsViewModel.TryNavigateBack();
        }

        if (_profileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _profileViewModel))
        {
            return _profileViewModel.TryNavigateBack();
        }

        if (_changeProfileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel))
        {
            if (_isStartupProfileSelection)
                return false;

            await NavigateBackFromChangeProfileAsync();
            return true;
        }

        if (ReferenceEquals(CurrentPageViewModel, LoginViewModel))
        {
            if (await LoginViewModel.TryNavigateBackAsync())
                return true;

            if (_isAddingProfile)
            {
                await NavigateBackFromAddProfileAsync();
                return true;
            }

            return false;
        }

        if (_registrationViewModel is not null && ReferenceEquals(CurrentPageViewModel, _registrationViewModel))
        {
            if (_isAddingProfile)
                await NavigateBackFromAddProfileAsync();
            else
                NavigateToLogin();

            return true;
        }

        return false;
    }

    public Task RequestLogoutAsync() => LogoutAsync();

    private void HandleHideVisibleSecretsRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HideVisibleSensitiveData();
            return;
        }

        Dispatcher.UIThread.Post(HideVisibleSensitiveData);
    }


    private void HideVisibleSensitiveData()
    {
        _passwordsViewModel?.HideVisibleSensitiveData();
    }


    private void ShowDatabaseRecoveryDialog(DatabaseVersionNotSupportedException exception)
    {
        _databaseVersionException = exception;
        _databaseRecoveryStage = DatabaseRecoveryStage.CompatibilityError;
        _isResettingDatabase = false;
        ClearStatusMessage();
        RaiseDatabaseRecoveryProperties();
    }

    private async Task HandleDatabaseRecoveryPrimaryActionAsync()
    {
        if (_isResettingDatabase || !IsDatabaseRecoveryDialogOpen)
            return;

        if (_databaseRecoveryStage != DatabaseRecoveryStage.FinalConfirmation)
        {
            _databaseRecoveryStage = DatabaseRecoveryStage.FinalConfirmation;
            RaiseDatabaseRecoveryProperties();
            return;
        }

        _isResettingDatabase = true;
        RaiseDatabaseRecoveryProperties();

        try
        {
            await _backendClient.ResetDatabaseAndRestartAsync(LifetimeToken);
            LifetimeToken.ThrowIfCancellationRequested();
            _databaseVersionException = null;
            _databaseRecoveryStage = DatabaseRecoveryStage.None;
            ClearStatusMessage();
            RaiseDatabaseRecoveryProperties();
        }
        catch
        {
            _databaseRecoveryStage = DatabaseRecoveryStage.ResetFailed;
        }
        finally
        {
            _isResettingDatabase = false;
            RaiseDatabaseRecoveryProperties();
        }
    }

    private void HandleDatabaseRecoverySecondaryAction()
    {
        if (_isResettingDatabase || !IsDatabaseRecoveryDialogOpen)
            return;

        _databaseRecoveryStage = _databaseRecoveryStage switch
        {
            DatabaseRecoveryStage.FinalConfirmation => DatabaseRecoveryStage.CompatibilityError,
            DatabaseRecoveryStage.ResetFailed => DatabaseRecoveryStage.CompatibilityError,
            DatabaseRecoveryStage.CompatibilityError => DatabaseRecoveryStage.Declined,
            _ => _databaseRecoveryStage
        };

        RaiseDatabaseRecoveryProperties();
    }

    private string BuildDatabaseVersionErrorMessage()
    {
        if (_databaseVersionException?.DetectedVersion is int detectedVersion)
        {
            return string.Format(
                GetTranslation("DatabaseVersion_Error_Message"),
                detectedVersion,
                _databaseVersionException.OldestSupportedVersion,
                _databaseVersionException.CurrentVersion);
        }

        return GetTranslation("DatabaseVersion_InvalidHeader_Message");
    }

    private void RaiseDatabaseRecoveryProperties()
    {
        this.RaisePropertyChanged(nameof(IsDatabaseRecoveryDialogOpen));
        this.RaisePropertyChanged(nameof(IsApplicationInteractionEnabled));
        this.RaisePropertyChanged(nameof(IsDatabaseResetInProgress));
        this.RaisePropertyChanged(nameof(IsDatabaseRecoverySecondaryButtonVisible));
        this.RaisePropertyChanged(nameof(IsDatabaseRecoveryPrimaryButtonVisible));
        this.RaisePropertyChanged(nameof(DatabaseRecoveryTitle));
        this.RaisePropertyChanged(nameof(DatabaseRecoveryMessage));
        this.RaisePropertyChanged(nameof(DatabaseRecoveryPrimaryButtonLabel));
        this.RaisePropertyChanged(nameof(DatabaseRecoverySecondaryButtonLabel));
        RaiseMobileSwipeEligibilityProperties();
    }

    protected override void OnLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(AppTitle));
        this.RaisePropertyChanged(nameof(SettingsLabel));
        this.RaisePropertyChanged(nameof(PasswordVaultLabel));
        this.RaisePropertyChanged(nameof(ProfileLabel));
        this.RaisePropertyChanged(nameof(DevicesLabel));
        this.RaisePropertyChanged(nameof(LogoutLabel));
        this.RaisePropertyChanged(nameof(ChangeProfileLabel));
        this.RaisePropertyChanged(nameof(RememberMeDropdownLabel));
        this.RaisePropertyChanged(nameof(RememberMeWarningLabel));
        this.RaisePropertyChanged(nameof(RememberMeWarningText));
        this.RaisePropertyChanged(nameof(RememberMeOnLabel));
        this.RaisePropertyChanged(nameof(RememberMeOffLabel));
        this.RaisePropertyChanged(nameof(WelcomeLabel));
        this.RaisePropertyChanged(nameof(NavigationLabel));
        this.RaisePropertyChanged(nameof(YesLabel));
        this.RaisePropertyChanged(nameof(NoLabel));
        this.RaisePropertyChanged(nameof(LogoutConfirmationTitle));
        this.RaisePropertyChanged(nameof(LogoutConfirmationMessage));
        this.RaisePropertyChanged(nameof(ExitConfirmationTitle));
        this.RaisePropertyChanged(nameof(ExitConfirmationMessage));
        this.RaisePropertyChanged(nameof(SessionRenewalWarningTitle));
        this.RaisePropertyChanged(nameof(SessionRenewalWarningMessage));
        this.RaisePropertyChanged(nameof(SessionRenewalYesButtonLabel));
        RaiseDatabaseRecoveryProperties();
        RaiseHeaderSubtitleProperties();
        this.RaisePropertyChanged(nameof(MobileCurrentPageLabel));
    }

    protected override void OnStatusMessageChanged()
    {
        RaiseHeaderSubtitleProperties();
    }

    private void RaiseHeaderSubtitleProperties()
    {
        this.RaisePropertyChanged(nameof(HeaderSubtitle));
        this.RaisePropertyChanged(nameof(HasHeaderSubtitle));
        this.RaisePropertyChanged(nameof(IsHeaderSubtitleUser));
        this.RaisePropertyChanged(nameof(IsHeaderSubtitleInformation));
        this.RaisePropertyChanged(nameof(IsHeaderSubtitleSuccess));
        this.RaisePropertyChanged(nameof(IsHeaderSubtitleError));
        this.RaisePropertyChanged(nameof(HasNonErrorHeaderSubtitle));
    }

    private OperationMessageKind HeaderSubtitleKind
    {
        get
        {
            if (TryGetShellHeaderStatus(out _, out var shellKind))
                return shellKind;

            if (TryGetCurrentPageHeaderStatus(out _, out var pageKind))
                return pageKind;

            return OperationMessageKind.None;
        }
    }

    private bool TryGetShellHeaderStatus(out string message, out OperationMessageKind kind)
    {
        message = StatusMessage ?? string.Empty;
        kind = GetStatusMessageKind(this);
        return !string.IsNullOrWhiteSpace(message) && kind != OperationMessageKind.None;
    }

    private bool TryGetCurrentPageHeaderStatus(out string message, out OperationMessageKind kind)
    {
        message = CurrentPageViewModel.StatusMessage ?? string.Empty;
        kind = GetStatusMessageKind(CurrentPageViewModel);

        if (string.IsNullOrWhiteSpace(message) || kind == OperationMessageKind.None)
            return false;

        return kind != OperationMessageKind.Error;
    }

    private static OperationMessageKind GetStatusMessageKind(ViewModelBase viewModel)
    {
        if (viewModel.IsStatusMessageError)
            return OperationMessageKind.Error;

        if (viewModel.IsStatusMessageSuccess)
            return OperationMessageKind.Success;

        if (viewModel.IsStatusMessageInformation)
            return OperationMessageKind.Information;

        return OperationMessageKind.None;
    }

    private void ObservePageStatus(ViewModelBase viewModel)
    {
        if (ReferenceEquals(_observedPageStatusViewModel, viewModel))
            return;

        StopObservingPageStatus();
        _observedPageStatusViewModel = viewModel;
        _observedPageStatusViewModel.PropertyChanged += HandlePageStatusChanged;
    }

    private void StopObservingPageStatus()
    {
        if (_observedPageStatusViewModel is null)
            return;

        _observedPageStatusViewModel.PropertyChanged -= HandlePageStatusChanged;
        _observedPageStatusViewModel = null;
    }

    private void HandlePageStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModelBase.StatusMessage)
            or nameof(ViewModelBase.HasStatusMessage)
            or nameof(ViewModelBase.IsStatusMessageInformation)
            or nameof(ViewModelBase.IsStatusMessageSuccess)
            or nameof(ViewModelBase.IsStatusMessageError)
            or nameof(ViewModelBase.HasNonErrorStatusMessage))
        {
            RaiseHeaderSubtitleProperties();
        }

        if (sender is PasswordsViewModel _ || sender is ProfileViewModel _)
            RaiseMobileSwipeEligibilityProperties();
    }

    private void ShowShellMessage(
        Func<string?>? messageFactory,
        OperationMessageKind kind = OperationMessageKind.Success)
    {
        if (messageFactory is null)
        {
            ClearStatusMessage();
            return;
        }

        Func<string> safeFactory = () => messageFactory() ?? string.Empty;
        switch (kind)
        {
            case OperationMessageKind.Error:
                ShowErrorMessage(safeFactory);
                break;
            case OperationMessageKind.Information:
                ShowInformationMessage(safeFactory);
                break;
            default:
                ShowSuccessMessage(safeFactory);
                break;
        }
    }

    private void HandleBackendRuntimeStateChanged(
        object? sender,
        BackendRuntimeStateChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyBackendRuntimeSnapshot(args.Current);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyBackendRuntimeSnapshot(args.Current));
    }

    private void ApplyBackendRuntimeSnapshot(BackendRuntimeSnapshot snapshot)
    {
        if (IsDisposed)
            return;
        ApplyBackendAvailability(snapshot.IsReady);

        if (IsAuthenticated)
            return;

        switch (snapshot.State)
        {
            case BackendRuntimeState.NotStarted:
            case BackendRuntimeState.Starting:
                ShowLocalizedInformationMessage("Shell_BackendStarting");
                break;
            case BackendRuntimeState.WaitingForDeviceUnlock:
                ShowLocalizedErrorMessage("Shell_DeviceUnlockRequired");
                break;
            case BackendRuntimeState.Failed
                when snapshot.FailureKind == BackendRuntimeFailureKind.DatabaseCompatibility &&
                     snapshot.Failure is DatabaseVersionNotSupportedException databaseException:
                ShowDatabaseRecoveryDialog(databaseException);
                break;
            case BackendRuntimeState.Failed:
                ShowLocalizedErrorMessage("Shell_BackendStartupFailed");
                break;
            case BackendRuntimeState.Ready:
                ClearStatusMessage();
                _ = InitializeAsync();
                break;
            case BackendRuntimeState.Stopping:
            case BackendRuntimeState.Stopped:
                ShowLocalizedErrorMessage("Shell_BackendStopped");
                break;
        }
    }

    private void ApplyBackendAvailability(bool isInitialized)
    {
        if (_isBackendInitialized == isInitialized)
            return;

        _isBackendInitialized = isInitialized;
        LoginViewModel.SetBackendInitialized(isInitialized);
        _registrationViewModel?.SetBackendInitialized(isInitialized);
    }

    private bool IsInformationViewModel(ViewModelBase page) =>
        ReferenceEquals(page, AboutViewModel) ||
        ReferenceEquals(page, ThirdPartyLicensesViewModel) ||
        ReferenceEquals(page, ThirdPartyLicenseDetailViewModel) ||
        page is HelpPageViewModel;

    private void NavigateToSettings()
    {
        if (IsSettingsPageVisible)
            return;
        var fromInformation = IsInformationPageVisible;
        if (!fromInformation)
            _settingsReturnPageViewModel = CurrentPageViewModel;
        var route = new InformationRoute(InformationDestination.Settings);
        _informationNavigation.Reset(route);
        ShowInformationRoute(route, fromInformation);
        if (!fromInformation)
            _ = SettingsViewModel.LoadBackgroundSyncStateAsync();
    }

    private void SetPageAfterSessionChange(ViewModelBase destination)
    {
        // Session cleanup still happens normally. Public documentation can remain
        // open, and Back returns to the newly valid authentication destination.
        if (IsSettingsSectionVisible)
            _settingsReturnPageViewModel = destination;
        else
            CurrentPageViewModel = destination;
    }

    private void NavigateBackFromSettings() => NavigateBackFromInformation();
    private void NavigateBackFromAbout() => NavigateBackFromInformation();
    private void NavigateBackFromThirdPartyLicenses() => NavigateBackFromInformation();
    private void NavigateBackFromThirdPartyLicenseDetail() => NavigateBackFromInformation();

    private void NavigateToAbout()
    {
        if (IsSettingsPageVisible)
            NavigateInformation(new InformationRoute(InformationDestination.About));
    }

    private void NavigateToHelp()
    {
        if (IsSettingsPageVisible)
            NavigateInformation(new InformationRoute(InformationDestination.Help));
    }

    private void NavigateToThirdPartyLicenses()
    {
        if (ReferenceEquals(CurrentPageViewModel, AboutViewModel))
            NavigateInformation(new InformationRoute(InformationDestination.Licenses));
    }

    private void NavigateToThirdPartyLicenseDetail(ThirdPartyNotice notice)
    {
        if (ReferenceEquals(CurrentPageViewModel, ThirdPartyLicensesViewModel))
            NavigateInformation(new InformationRoute(InformationDestination.LicenseDetail, notice.Id));
    }

    /// <summary>Navigation-only entry point for stable guide IDs. Contextual screen buttons are added in Phase 7.</summary>
    public void OpenHelpTopic(string? topicId) =>
        NavigateInformation(new InformationRoute(InformationDestination.HelpTopic, topicId));

    private void NavigateInformation(InformationRoute requested)
    {
        if (IsDisposed)
            return;
        var route = requested;
        var missingTopic = route.Destination == InformationDestination.HelpTopic &&
            (route.ContentId is null || !_helpCatalog.ForLanguage(CurrentLanguage).Topics.ContainsKey(route.ContentId));
        if (missingTopic)
            route = new InformationRoute(InformationDestination.Help);

        if (!IsSettingsSectionVisible)
        {
            // Keep the existing caller itself; never copy a form or inspect its fields.
            _settingsReturnPageViewModel = CurrentPageViewModel;
            _informationNavigation.Reset(route);
        }
        else
        {
            _informationNavigation.Navigate(route);
        }
        ShowInformationRoute(route, reversed: false, missingTopic: missingTopic);
    }

    private void NavigateBackFromInformation()
    {
        if (!IsSettingsSectionVisible)
            return;
        if (_informationNavigation.TryGoBack(out var route))
        {
            ShowInformationRoute(route!, reversed: true);
            return;
        }
        var caller = _settingsReturnPageViewModel ?? LoginViewModel;
        _settingsReturnPageViewModel = null;
        _informationNavigation.Clear();
        if (!IsAuthenticated && (ReferenceEquals(caller, _profileViewModel) || ReferenceEquals(caller, _passwordsViewModel)))
            caller = LoginViewModel;
        IsPageTransitionReversed = true;
        CurrentPageViewModel = caller;
    }

    private void ShowInformationRoute(InformationRoute route, bool reversed, bool missingTopic = false)
    {
        ViewModelBase page;
        switch (route.Destination)
        {
            case InformationDestination.Settings:
                page = SettingsViewModel;
                break;
            case InformationDestination.About:
                page = AboutViewModel;
                break;
            case InformationDestination.Licenses:
                page = ThirdPartyLicensesViewModel;
                break;
            case InformationDestination.LicenseDetail:
                ThirdPartyLicenseDetailViewModel.SetNotice(_noticeCatalog.Notices.Single(notice => notice.Id == route.ContentId));
                page = ThirdPartyLicenseDetailViewModel;
                break;
            default:
                if (!_helpPages.TryGetValue(route, out var helpPage))
                {
                    helpPage = new HelpPageViewModel(UiPreferences, _helpCatalog, route,
                        NavigateBackFromInformation, NavigateInformation);
                    _helpPages.Add(route, helpPage);
                }
                helpPage.SetTopicUnavailable(missingTopic);
                page = helpPage;
                break;
        }
        IsPageTransitionReversed = reversed;
        ClearStatusMessage();
        CurrentPageViewModel = page;
    }

    private void NavigateToLogin()
    {
        ConfigureAuthBackNavigation();
        CurrentPageViewModel = LoginViewModel;
        ClearStatusMessage();
    }

    private void NavigateToRegistration()
    {
        var registrationViewModel = RegistrationViewModel;
        ConfigureAuthBackNavigation();
        CurrentPageViewModel = registrationViewModel;
        ClearStatusMessage();
    }

    private async Task ShowStartupProfileSelectionAsync(Func<string?>? messageFactory = null)
    {
        _isAddingProfile = false;
        _isStartupProfileSelection = true;
        _profileChangeReturnPageIndex = 0;
        _authSessionRegistry.CurrentUserToken = Guid.Empty;
        ApplyRememberMeFromSession(false);
        IsAuthenticated = false;
        CurrentUserDisplayName = string.Empty;
        CurrentUserSubtitle = string.Empty;
        _passwordsViewModel?.Reset();
        _profileViewModel?.Reset();
        ChangeProfileViewModel.SetStartupSelectionMode(true);
        ConfigureAuthBackNavigation();
        await ChangeProfileViewModel.LoadAsync();
        SetPageAfterSessionChange(ChangeProfileViewModel);
        ShowShellMessage(messageFactory, OperationMessageKind.Information);
    }

    private async Task ShowChangeProfileAsync()
    {
        if (!IsAuthenticated)
            return;

        _isAddingProfile = false;
        _isStartupProfileSelection = false;
        if (IsMainContentPageVisible)
            _profileChangeReturnPageIndex = CurrentMainPageIndex;

        ChangeProfileViewModel.SetStartupSelectionMode(false);
        ConfigureAuthBackNavigation();
        await ChangeProfileViewModel.LoadAsync();
        CurrentPageViewModel = ChangeProfileViewModel;
        ClearStatusMessage();
    }

    private void NavigateToLoginAnotherProfile()
    {
        if (!IsAuthenticated && !_isStartupProfileSelection)
            return;

        _isAddingProfile = true;
        LoginViewModel.Reset();
        _registrationViewModel?.Reset();
        ConfigureAuthBackNavigation();
        CurrentPageViewModel = LoginViewModel;
        ClearStatusMessage();
    }

    private void ConfigureAuthBackNavigation()
    {
        var isVisible = _isAddingProfile;
        LoginViewModel.SetBackNavigation(isVisible, isVisible ? NavigateBackFromAddProfileAsync : null);
        _registrationViewModel?.SetBackNavigation(isVisible ? NavigateBackFromAddProfileAsync : null);
    }

    private async Task NavigateBackFromAddProfileAsync()
    {
        var wasStartupProfileSelection = _isStartupProfileSelection;
        _isAddingProfile = false;
        ConfigureAuthBackNavigation();
        LoginViewModel.Reset();
        _registrationViewModel?.Reset();

        if (wasStartupProfileSelection)
        {
            await ShowStartupProfileSelectionAsync();
            return;
        }

        await ShowChangeProfileAsync();
    }

    private async Task NavigateBackFromChangeProfileAsync()
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token == Guid.Empty)
        {
            await HandleLoggedOutStateAsync();
            return;
        }

        await LoadAuthenticatedStateAsync(token);
        RestoreProfileChangeReturnPage();
    }

    private async Task SwitchToProfileAsync(Guid token)
    {
        if (!_authSessionRegistry.ListTokens().Contains(token))
            return;

        var status = await _endpoints.GetAuthSessionStatusAsync(token);
        if (!status.IsAuthenticated)
        {
            await HandleInvalidatedSessionAsync(token, status.InvalidationReason);
            return;
        }
        SetSessionExpiry(token, status.ExpiresAtUtc);

        var wasStartupSelection = _isStartupProfileSelection;
        _isStartupProfileSelection = false;
        if (!await LoadAuthenticatedStateAsync(token))
            return;

        if (!wasStartupSelection)
            RestoreProfileChangeReturnPage();

        ShowLocalizedSuccessMessage(wasStartupSelection
            ? "Shell_RememberedSessionLoaded"
            : "Shell_ProfileChanged");
    }

    private void RestoreProfileChangeReturnPage()
    {
        switch (_profileChangeReturnPageIndex)
        {
            case 1:
                NavigateToDevices();
                break;
            case 2:
                NavigateToProfile();
                break;
            default:
                NavigateToPasswords();
                break;
        }
    }

    private void NavigateToMainPageFromMobileDropdown(int targetMainPageIndex)
    {
        if (!IsAuthenticated)
        {
            return;
        }

        ConfigureDirectMobileMainPageTransition(targetMainPageIndex);

        NavigateToMainPage(targetMainPageIndex);
    }

    private void NavigateToMainPageFromMobileCarousel(int targetMainPageIndex)
    {
        if (!IsAuthenticated || !IsMainContentPageVisible)
            return;

        NavigateToMainPage(targetMainPageIndex);
    }

    private void NavigateToMainPage(int targetMainPageIndex)
    {
        switch (targetMainPageIndex)
        {
            case 1:
                NavigateToDevices();
                break;
            case 2:
                NavigateToProfile();
                break;
            default:
                NavigateToPasswords();
                break;
        }
    }

    private void ConfigureDirectMobileMainPageTransition(int targetMainPageIndex)
    {
        if (!IsMobileNavigationEnabled || !IsMainContentPageVisible)
        {
            return;
        }

        var currentMainPageIndex = CurrentMainPageIndex;
        if (currentMainPageIndex == targetMainPageIndex)
        {
            return;
        }

        IsPageTransitionReversed = targetMainPageIndex < currentMainPageIndex;
    }

    private void NavigateToPasswords()
    {
        if (!IsAuthenticated)
        {
            return;
        }

        var isReselectingPasswords = IsPasswordsMainPageSelected;

        ClearStatusMessage();
        PasswordsViewModel.ShowMainPage();
        ProfileViewModel.DiscardTransientNavigationState();
        ShowMainContentPage(PasswordsViewModel);
        SetCurrentMobileNavigationIndex(0);

        if (isReselectingPasswords)
        {
            PasswordsViewModel.RequestListScrollToTop();
        }
    }

    private void NavigateToProfile()
    {
        if (!IsAuthenticated)
        {
            return;
        }

        ClearStatusMessage();
        PasswordsViewModel.ShowMainPage();
        ProfileViewModel.ShowProfileMainPage();
        ShowMainContentPage(ProfileViewModel);
        SetCurrentMobileNavigationIndex(2);
    }

    private void NavigateToDevices()
    {
        if (!IsAuthenticated)
        {
            return;
        }

        var isReselectingDevices = IsDevicesMainPageSelected;

        ClearStatusMessage();
        PasswordsViewModel.ShowMainPage();
        ProfileViewModel.ShowDevicesMainPage();
        ShowMainContentPage(ProfileViewModel);
        SetCurrentMobileNavigationIndex(1);

        if (isReselectingDevices)
        {
            ProfileViewModel.RequestDeviceListScrollToTop();
        }
    }

    private void ShowMainContentPage(ViewModelBase pageViewModel)
    {
        if (!ReferenceEquals(CurrentPageViewModel, pageViewModel))
        {
            CurrentPageViewModel = pageViewModel;
            return;
        }

        CurrentPageContentViewModel = new MainPageContentViewModel(pageViewModel);
        RaiseNavigationStateProperties();
    }

    public bool NavigateToNextMainPage()
    {
        if (!IsAuthenticated || !IsMainContentPageVisible)
        {
            return false;
        }

        IsPageTransitionReversed = false;

        return CurrentMainPageIndex switch
        {
            0 => NavigateToDevicesFromSwipe(),
            1 => NavigateToProfileFromSwipe(),
            _ => NavigateToPasswordsFromSwipe()
        };
    }

    public bool NavigateToPreviousMainPage()
    {
        if (!IsAuthenticated || !IsMainContentPageVisible)
        {
            return false;
        }

        IsPageTransitionReversed = true;

        return CurrentMainPageIndex switch
        {
            0 => NavigateToProfileFromSwipe(),
            1 => NavigateToPasswordsFromSwipe(),
            _ => NavigateToDevicesFromSwipe()
        };
    }

    private bool NavigateToPasswordsFromSwipe()
    {
        NavigateToPasswords();
        return true;
    }

    private bool NavigateToDevicesFromSwipe()
    {
        NavigateToDevices();
        return true;
    }

    private bool NavigateToProfileFromSwipe()
    {
        NavigateToProfile();
        return true;
    }

    private async Task<bool> TryAddRememberedSessionAsync(Guid token, bool select)
    {
        if (token == Guid.Empty)
            return false;

        try
        {
            var status = await _endpoints.GetAuthSessionStatusAsync(token);
            if (!status.IsAuthenticated)
                return false;

            var profile = await _endpoints.GetUserProfileInfoAsync(token);
            if (await ContainsActiveUserIdAsync(profile.UId, token))
            {
                try { await _endpoints.LogoutAsync(token); } catch { }
                return false;
            }

            _authSessionRegistry.TryAdd(token, select);
            SetSessionProfile(token, profile);
            SetSessionExpiry(token, status.ExpiresAtUtc);
            return true;
        }
        catch
        {
            try { await _endpoints.LogoutAsync(token); } catch { }
            _authSessionRegistry.TryRemove(token);
            return false;
        }
    }


    private void ApplyRememberMeFromSession(bool isEnabled)
    {
        _isApplyingRememberMeFromSession = true;
        try
        {
            IsCurrentRememberMeEnabled = isEnabled;
        }
        finally
        {
            _isApplyingRememberMeFromSession = false;
        }
    }


    private async Task SetCurrentRememberMeAsync(bool isEnabled, bool previousValue)
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token == Guid.Empty || !IsAuthenticated)
        {
            ApplyRememberMeFromSession(previousValue);
            return;
        }

        ClearStatusMessage();

        try
        {
            IsSettingRememberMe = true;
            await _endpoints.SetRememberMeAsync(token, isEnabled);
            _authSessionRegistry.TrySetRememberMe(token, isEnabled);
            ScheduleSessionExpiryWarning(token);
            ShowLocalizedSuccessMessage(isEnabled
                ? "Shell_RememberMeEnabled"
                : "Shell_RememberMeDisabled");
        }
        catch
        {
            ApplyRememberMeFromSession(previousValue);
            ShowLocalizedErrorMessage("Shell_RememberMeUpdateFailed");
        }
        finally
        {
            IsSettingRememberMe = false;
        }
    }


    private async Task OnAuthenticationSucceededAsync(Guid token)
    {
        if (IsDisposed)
            return;
        var profileTask = _endpoints.GetUserProfileInfoAsync(token);
        var statusTask = _endpoints.GetAuthSessionStatusAsync(token);
        await Task.WhenAll(profileTask, statusTask);
        var profile = await profileTask;
        var status = await statusTask;
        if (!status.IsAuthenticated)
            throw new InvalidTokenException();
        var wasAddingProfile = _isAddingProfile;
        var wasStartupProfileSelection = _isStartupProfileSelection;

        if (await ContainsActiveUserIdAsync(profile.UId, token))
        {
            try { await _endpoints.LogoutAsync(token); } catch { }
            throw new DuplicateActiveProfileException();
        }

        _authSessionRegistry.TryAdd(token);
        SetSessionProfile(token, profile);
        SetSessionExpiry(token, status.ExpiresAtUtc);
        _isAddingProfile = false;
        _isStartupProfileSelection = false;
        ConfigureAuthBackNavigation();
        if (!await LoadAuthenticatedStateAsync(token, profile))
            return;

        if (wasAddingProfile && !wasStartupProfileSelection)
            RestoreProfileChangeReturnPage();

        ShowLocalizedSuccessMessage("Shell_SignedIn");
    }

    private async Task<bool> LoadAuthenticatedStateAsync(
        Guid token,
        Func<string?>? messageFactory = null,
        OperationMessageKind messageKind = OperationMessageKind.Success)
    {
        var profile = await _endpoints.GetUserProfileInfoAsync(token);
        return await LoadAuthenticatedStateAsync(token, profile, messageFactory, messageKind);
    }

    private async Task<bool> LoadAuthenticatedStateAsync(
        Guid token,
        UserProfileInfoResponse profile,
        Func<string?>? messageFactory = null,
        OperationMessageKind messageKind = OperationMessageKind.Success)
    {
        var previousToken = _authSessionRegistry.CurrentUserToken;
        var previousUserId = CurrentUserId;
        _authSessionRegistry.CurrentUserToken = token;
        if (previousToken != token)
            NotifyRefreshAuthenticationChanged();
        SetSessionProfile(token, profile);
        if (IsAuthenticated && previousUserId != CurrentUserId)
            this.RaisePropertyChanged(nameof(CurrentUserId));
        IsAuthenticated = true;
        CurrentUserDisplayName = BuildDisplayName(profile);
        CurrentUserSubtitle = BuildSubtitle(profile);
        ApplyRememberMeFromSession(profile.IsRememberMeEnabled);

        var passwordsViewModel = PasswordsViewModel;
        var profileViewModel = ProfileViewModel;
        passwordsViewModel.Reset();
        profileViewModel.Reset();

        var passwordsLoadTask = passwordsViewModel.LoadAsync(token);
        var profileLoadTask = profileViewModel.LoadAsync(token, profile);
        await Task.WhenAll(passwordsLoadTask, profileLoadTask);
        var passwordsLoaded = await passwordsLoadTask;
        var profileLoaded = await profileLoadTask;

        SetPageAfterSessionChange(passwordsViewModel);
        SetCurrentMobileNavigationIndex(0);

        if (!passwordsLoaded)
        {
            var errorMessageFactory = passwordsViewModel.CaptureStatusMessageFactory()
                ?? (() => GetTranslation("Error_Generic"));
            passwordsViewModel.ClearStatusMessage();
            ShowErrorMessage(errorMessageFactory);
        }
        else if (!profileLoaded)
        {
            var errorMessageFactory = profileViewModel.CaptureStatusMessageFactory()
                ?? (() => GetTranslation("Error_Generic"));
            profileViewModel.ClearStatusMessage();
            ShowErrorMessage(errorMessageFactory);
        }
        else
        {
            ShowShellMessage(messageFactory, messageKind);
        }

        return passwordsLoaded && profileLoaded;
    }

    private async Task<bool> RefreshProfileDataAsync()
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token == Guid.Empty)
            return false;

        var profile = await _endpoints.GetUserProfileInfoAsync(token);

        CurrentUserDisplayName = BuildDisplayName(profile);
        CurrentUserSubtitle = BuildSubtitle(profile);
        ApplyRememberMeFromSession(profile.IsRememberMeEnabled);
        SetSessionProfile(token, profile);

        return await ProfileViewModel.LoadAsync(token, profile)
            && await ProfileViewModel.RefreshLoadedDevicesAsync();
    }

    private async Task LogoutAsync()
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token == Guid.Empty)
        {
            await HandleLoggedOutStateAsync();
            return;
        }

        try
        {
            await _endpoints.LogoutAsync(token);
        }
        catch
        {
        }
        finally
        {
            _authSessionRegistry.TryRemove(token);
            RemoveSessionDeadlineTimer(token);
            _failedRememberedRenewalExpiries.Remove(token);
            await LoadNextAvailableSessionOrLoginAsync(
                () => GetTranslation("Shell_LoggedOut"));
        }
    }

    private async Task HandleAccountDeletedAsync()
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token != Guid.Empty)
        {
            _authSessionRegistry.TryRemove(token);
            RemoveSessionDeadlineTimer(token);
            _failedRememberedRenewalExpiries.Remove(token);
        }

        await LoadNextAvailableSessionOrLoginAsync(
            () => GetTranslation("Profile_Delete_Success"));
    }

    private Task HandleLoggedOutStateAsync(
        Func<string?>? messageFactory = null,
        OperationMessageKind messageKind = OperationMessageKind.Success)
    {
        StopSessionDeadlineTimers();
        IsSessionRenewalDialogOpen = false;
        IsRenewingSession = false;
        _sessionRenewalDialogToken = Guid.Empty;
        _sessionRenewalPromptShownForToken = Guid.Empty;
        _sessionRenewalDialogProfileName = string.Empty;
        _isAddingProfile = false;
        _isStartupProfileSelection = false;
        ConfigureAuthBackNavigation();
        IsAuthenticated = false;
        NotifyRefreshAuthenticationChanged();
        CurrentUserDisplayName = string.Empty;
        CurrentUserSubtitle = string.Empty;
        ApplyRememberMeFromSession(false);
        _passwordsViewModel?.Reset();
        _profileViewModel?.Reset();
        SetCurrentMobileNavigationIndex(0);
        LoginViewModel.Reset();
        _registrationViewModel?.Reset();
        SetPageAfterSessionChange(LoginViewModel);
        ShowShellMessage(
            messageFactory ?? (() => GetTranslation("Shell_LoggedOut")),
            messageKind);
        return Task.CompletedTask;
    }

    private void SetSessionExpiry(Guid token, DateTimeOffset? expiresAtUtc)
    {
        if (!_authSessionRegistry.TrySetExpiresAtUtc(token, expiresAtUtc))
            return;

        if (expiresAtUtc is null)
        {
            RemoveSessionDeadlineTimer(token);
            _failedRememberedRenewalExpiries.Remove(token);
            return;
        }

        if (_failedRememberedRenewalExpiries.TryGetValue(token, out var attemptedExpiry) &&
            attemptedExpiry != expiresAtUtc.Value)
        {
            _failedRememberedRenewalExpiries.Remove(token);
        }

        ScheduleSessionExpiryWarning(token);
    }

    private void ScheduleSessionExpiryWarning(Guid token)
    {
        RemoveSessionDeadlineTimer(token);
        if (IsDisposed || LifetimeToken.IsCancellationRequested ||
            _authSessionRegistry.GetSession(token)?.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return;
        }

        // A past expiry is already an advisory deadline. A status read that still
        // reports this session as authenticated must not create an immediate loop.
        if (expiresAtUtc <= DateTimeOffset.UtcNow)
            return;

        var deadline = SessionExpiryWarningPolicy.GetWarningDeadline(expiresAtUtc, SessionRenewalWarningLeadTime);
        if (deadline < DateTimeOffset.UtcNow)
            deadline = DateTimeOffset.UtcNow;

        IDisposable? scheduled = null;
        scheduled = _deadlineScheduler.Schedule(deadline, () =>
        {
            if (!_sessionDeadlineTimers.TryGetValue(token, out var active) || !ReferenceEquals(active, scheduled))
                return;

            _sessionDeadlineTimers.Remove(token);
            _ = HandleSessionExpiryDeadlineAsync(token, expiresAtUtc);
        });
        _sessionDeadlineTimers[token] = scheduled;
    }

    private void ScheduleSessionExpiryBoundaryCheck(Guid token, DateTimeOffset expiresAtUtc)
    {
        RemoveSessionDeadlineTimer(token);
        if (IsDisposed || LifetimeToken.IsCancellationRequested)
            return;

        IDisposable? scheduled = null;
        scheduled = _deadlineScheduler.Schedule(expiresAtUtc, () =>
        {
            if (!_sessionDeadlineTimers.TryGetValue(token, out var active) || !ReferenceEquals(active, scheduled))
                return;

            _sessionDeadlineTimers.Remove(token);
            _ = RevalidateSessionAtBoundaryAsync(token);
        });
        _sessionDeadlineTimers[token] = scheduled;
    }

    private async Task RevalidateSessionAtBoundaryAsync(Guid token)
    {
        try { await RevalidateSessionTokenAsync(token, LifetimeToken); }
        catch { }
    }

    private async Task HandleSessionExpiryDeadlineAsync(Guid token, DateTimeOffset expectedExpiry)
    {
        if (IsDisposed || _authSessionRegistry.GetSession(token)?.ExpiresAtUtc != expectedExpiry)
            return;

        if (!SessionExpiryWarningPolicy.IsWarningDue(
                DateTimeOffset.UtcNow,
                expectedExpiry,
                SessionRenewalWarningLeadTime))
        {
            ScheduleSessionExpiryWarning(token);
            return;
        }

        if (DateTimeOffset.UtcNow >= expectedExpiry)
        {
            try { await RevalidateSessionTokenAsync(token, LifetimeToken); }
            catch { }
            return;
        }

        await HandleSessionExpirationWarningAsync(token, expectedExpiry);
    }

    private async Task RevalidateSessionTokenAsync(Guid token, CancellationToken cancellationToken)
    {
        if (IsDisposed || _authSessionRegistry.GetSession(token) is null)
            return;

        var status = await _endpoints.GetAuthSessionStatusAsync(token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed || _authSessionRegistry.GetSession(token) is null)
            return;

        if (!status.IsAuthenticated)
        {
            if (!_autoRenewingSessionTokens.Contains(token))
                await HandleInvalidatedSessionAsync(token, status.InvalidationReason);
            return;
        }

        SetSessionExpiry(token, status.ExpiresAtUtc);
    }

    private void RemoveSessionDeadlineTimer(Guid token)
    {
        if (_sessionDeadlineTimers.Remove(token, out var timer))
            timer.Dispose();
    }

    private void StopSessionDeadlineTimers()
    {
        foreach (var timer in _sessionDeadlineTimers.Values)
            timer.Dispose();
        _sessionDeadlineTimers.Clear();
        _failedRememberedRenewalExpiries.Clear();
    }


    private async Task HandleSessionExpirationWarningAsync(Guid token, DateTimeOffset expiresAtUtc)
    {
        if (!SessionExpiryWarningPolicy.IsWarningDue(
                DateTimeOffset.UtcNow,
                expiresAtUtc,
                SessionRenewalWarningLeadTime))
            return;

        var session = _authSessionRegistry.GetSession(token);
        if (session?.IsRememberMeEnabled == true)
        {
            if (_failedRememberedRenewalExpiries.TryGetValue(token, out var attemptedExpiry) &&
                attemptedExpiry == expiresAtUtc)
            {
                ScheduleSessionExpiryBoundaryCheck(token, expiresAtUtc);
                return;
            }

            _failedRememberedRenewalExpiries[token] = expiresAtUtc;
            await AutoRenewRememberedSessionAsync(token);
            return;
        }

        ShowSessionRenewalWarning(token);
    }


    private void ShowSessionRenewalWarning(Guid token)
    {
        if (IsSessionRenewalDialogOpen || _sessionRenewalPromptShownForToken == token)
            return;

        _sessionRenewalDialogToken = token;
        _sessionRenewalPromptShownForToken = token;
        _sessionRenewalDialogProfileName = GetSessionDisplayName(token);
        this.RaisePropertyChanged(nameof(SessionRenewalWarningMessage));
        IsSessionRenewalDialogOpen = true;
    }


    private async Task AutoRenewRememberedSessionAsync(Guid token)
    {
        if (!_autoRenewingSessionTokens.Add(token))
            return;

        var renewalFailed = false;
        try
        {
            await RenewSessionTokenAsync(token);
        }
        catch
        {
            renewalFailed = true;
        }
        finally
        {
            _autoRenewingSessionTokens.Remove(token);
        }

        if (renewalFailed)
        {
            try { await RevalidateSessionTokenAsync(token, LifetimeToken); }
            catch { }
        }
    }


    private async Task ConfirmSessionRenewalAsync()
    {
        if (IsRenewingSession)
            return;

        var oldToken = _sessionRenewalDialogToken;
        if (oldToken == Guid.Empty || _authSessionRegistry.GetSession(oldToken) is null || !IsAuthenticated)
        {
            CloseSessionRenewalDialog();
            return;
        }

        ClearStatusMessage();
        if (!_autoRenewingSessionTokens.Add(oldToken))
            return;

        var renewalFailed = false;
        try
        {
            IsRenewingSession = true;
            await RenewSessionTokenAsync(oldToken);
            CloseSessionRenewalDialog(true);
            ShowLocalizedSuccessMessage("Shell_SessionRenewed");
        }
        catch
        {
            renewalFailed = true;
            CloseSessionRenewalDialog();
            ShowLocalizedErrorMessage("Shell_SessionRenewalFailed");
        }
        finally
        {
            _autoRenewingSessionTokens.Remove(oldToken);
            IsRenewingSession = false;
        }

        if (renewalFailed)
        {
            try { await RevalidateSessionTokenAsync(oldToken, LifetimeToken); }
            catch { }
        }
    }


    private async Task RenewSessionTokenAsync(Guid oldToken)
    {
        var wasCurrent = oldToken == _authSessionRegistry.CurrentUserToken;
        var newToken = await _endpoints.RenewAuthSessionAsync(oldToken);
        RemoveSessionDeadlineTimer(oldToken);
        _failedRememberedRenewalExpiries.Remove(oldToken);

        if (!_authSessionRegistry.TryReplaceToken(oldToken, newToken))
        {
            _authSessionRegistry.TryAdd(newToken, wasCurrent);
            _authSessionRegistry.TryRemove(oldToken);
        }

        if (wasCurrent)
        {
            _authSessionRegistry.CurrentUserToken = newToken;
            PasswordsViewModel.SetSessionToken(newToken);
            ProfileViewModel.SetSessionToken(newToken);
            NotifyRefreshAuthenticationChanged();
        }

        if (_sessionRenewalPromptShownForToken == oldToken)
            _sessionRenewalPromptShownForToken = Guid.Empty;

        try { await RevalidateSessionTokenAsync(newToken, LifetimeToken); }
        catch { }

        if (_changeProfileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel))
            await _changeProfileViewModel.LoadAsync();
    }


    private void DeclineSessionRenewal()
    {
        if (IsRenewingSession)
            return;

        CloseSessionRenewalDialog();
        ShowLocalizedInformationMessage("Shell_SessionRenewalDeclined", true);
    }


    private async Task HandleInvalidatedSessionAsync(Guid token, AuthSessionInvalidationReason reason)
    {
        RemoveSessionDeadlineTimer(token);
        _failedRememberedRenewalExpiries.Remove(token);
        var wasCurrent = token == _authSessionRegistry.CurrentUserToken;
        Func<string?> messageFactory = () => GetSessionInvalidationMessage(reason);
        var session = _authSessionRegistry.GetSession(token);

        if (reason == AuthSessionInvalidationReason.Expired && session?.IsRememberMeEnabled == true)
        {
            if (await TryRestoreRememberedSessionAsync(token, session, wasCurrent))
                return;
        }

        _authSessionRegistry.TryRemove(token);

        if (_sessionRenewalDialogToken == token)
            CloseSessionRenewalDialog();

        if (wasCurrent)
        {
            await LoadNextAvailableSessionOrLoginAsync(messageFactory, OperationMessageKind.Error);
            return;
        }

        if (_authSessionRegistry.ListTokens().Count == 0)
        {
            await HandleLoggedOutStateAsync(messageFactory, OperationMessageKind.Error);
            return;
        }

        if (_changeProfileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel))
            await _changeProfileViewModel.LoadAsync();
    }


    private async Task<bool> TryRestoreRememberedSessionAsync(Guid oldToken, AuthSessionProfile session, bool wasCurrent)
    {
        if (session.UserId == Guid.Empty)
            return false;

        try
        {
            var newToken = await _endpoints.InitializeRememberMeSessionAsync(session.UserId);
            var status = await _endpoints.GetAuthSessionStatusAsync(newToken);
            if (!status.IsAuthenticated)
                return false;

            var profile = await _endpoints.GetUserProfileInfoAsync(newToken);

            if (!_authSessionRegistry.TryReplaceToken(oldToken, newToken))
            {
                _authSessionRegistry.TryAdd(newToken, wasCurrent);
                _authSessionRegistry.TryRemove(oldToken);
            }

            SetSessionProfile(newToken, profile);
            SetSessionExpiry(newToken, status.ExpiresAtUtc);

            if (_sessionRenewalDialogToken == oldToken)
                CloseSessionRenewalDialog(true);

            if (wasCurrent)
            {
                await LoadAuthenticatedStateAsync(
                    newToken,
                    profile,
                    () => GetTranslation("Shell_RememberedSessionLoaded"));
                return true;
            }

            if (_changeProfileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel))
                await _changeProfileViewModel.LoadAsync();

            return true;
        }
        catch
        {
            return false;
        }
    }



    private async Task LoadNextAvailableSessionOrLoginAsync(
        Func<string?>? messageFactory = null,
        OperationMessageKind messageKind = OperationMessageKind.Success)
    {
        if (IsDisposed)
            return;
        var nextToken = _authSessionRegistry.CurrentUserToken;
        if (nextToken == Guid.Empty)
        {
            await HandleLoggedOutStateAsync(messageFactory, messageKind);
            return;
        }

        await LoadAuthenticatedStateAsync(nextToken, messageFactory, messageKind);
    }


    private void CloseSessionRenewalDialog(bool resetPromptToken = false)
    {
        IsSessionRenewalDialogOpen = false;
        _sessionRenewalDialogToken = Guid.Empty;
        _sessionRenewalDialogProfileName = string.Empty;

        if (resetPromptToken)
            _sessionRenewalPromptShownForToken = Guid.Empty;

        this.RaisePropertyChanged(nameof(SessionRenewalWarningMessage));
    }


    private string GetSessionDisplayName(Guid token)
    {
        var session = _authSessionRegistry.GetSession(token);
        if (!string.IsNullOrWhiteSpace(session?.DisplayName))
            return session.DisplayName;

        if (token == _authSessionRegistry.CurrentUserToken && !string.IsNullOrWhiteSpace(CurrentUserDisplayName))
            return CurrentUserDisplayName;

        return GetTranslation("Profiles_UnknownProfile");
    }


    private void SetSessionProfile(Guid token, UserProfileInfoResponse profile)
    {
        var isSelectedToken = token == _authSessionRegistry.CurrentUserToken;
        var previousUserId = isSelectedToken
            ? _authSessionRegistry.GetSession(token)?.UserId
            : null;
        if (_authSessionRegistry.GetSession(token) is null)
            _authSessionRegistry.TryAdd(token);

        _authSessionRegistry.TrySetProfile(
            token,
            profile.UId,
            BuildDisplayName(profile),
            BuildSubtitle(profile),
            profile.Username,
            profile.Email,
            profile.IsRememberMeEnabled);
        ScheduleSessionExpiryWarning(token);
        var currentUserId = isSelectedToken
            ? _authSessionRegistry.GetSession(token)?.UserId
            : null;
        if (isSelectedToken && IsAuthenticated && previousUserId != currentUserId)
        {
            this.RaisePropertyChanged(nameof(CurrentUserId));
            NotifyRefreshAuthenticationChanged();
        }
    }


    private async Task<bool> ContainsActiveUserIdAsync(Guid userId, Guid excludedToken = default)
    {
        if (_authSessionRegistry.ContainsUserId(userId, excludedToken))
            return true;

        foreach (var token in _authSessionRegistry.ListTokens().Where(token => token != excludedToken).ToList())
        {
            var session = _authSessionRegistry.GetSession(token);
            if (session?.UserId == userId)
                return true;

            try
            {
                var profile = await _endpoints.GetUserProfileInfoAsync(token);
                SetSessionProfile(token, profile);

                if (profile.UId == userId)
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private string GetSessionInvalidationMessage(AuthSessionInvalidationReason reason) =>
        reason switch
        {
            AuthSessionInvalidationReason.LocalDataVerificationFailed => GetTranslation("Shell_LocalDataVerificationFailed"),
            AuthSessionInvalidationReason.ProfilePasswordChanged => GetTranslation("Shell_ProfilePasswordChangedLoggedOut"),
            AuthSessionInvalidationReason.ProfileRemoved => GetTranslation("Shell_ProfileRemovedLoggedOut"),
            AuthSessionInvalidationReason.Expired => GetTranslation("Shell_SessionExpired"),
            _ => GetTranslation("Shell_LoggedOut")
        };


    private int CurrentMainPageIndex
    {
        get
        {
            if (_passwordsViewModel is not null && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel))
            {
                return 0;
            }

            if (_profileViewModel is not null &&
                ReferenceEquals(CurrentPageViewModel, _profileViewModel) &&
                _profileViewModel.IsDevicesMainPage)
            {
                return 1;
            }

            if (_profileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _profileViewModel))
            {
                return 2;
            }

            return 0;
        }
    }

    private void RaiseNavigationStateProperties()
    {
        this.RaisePropertyChanged(nameof(IsPasswordsMainPageSelected));
        this.RaisePropertyChanged(nameof(IsDevicesMainPageSelected));
        this.RaisePropertyChanged(nameof(IsProfileMainPageSelected));
        this.RaisePropertyChanged(nameof(IsSettingsPageVisible));
        this.RaisePropertyChanged(nameof(IsInformationPageVisible));
        this.RaisePropertyChanged(nameof(IsSettingsSectionVisible));
        this.RaisePropertyChanged(nameof(IsDesktopNavigationVisible));
        this.RaisePropertyChanged(nameof(IsMobilePageIndicatorVisible));
        this.RaisePropertyChanged(nameof(IsMobileMainContentVisible));
        this.RaisePropertyChanged(nameof(IsMobileStandaloneContentVisible));
        this.RaisePropertyChanged(nameof(CurrentMobileStandalonePageViewModel));
        RaiseMobileSwipeEligibilityProperties();
    }


    public void RefreshPlatformAnimationAvailability() =>
        UiPreferences.MotionPolicy.RefreshPlatformState();

    protected override void OnInterfaceMotionChanged() =>
        RaiseMobileAnimationProperties();

    private bool IsCurrentMainPageAtSwipeRoot()
    {
        if (_passwordsViewModel is not null && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel))
        {
            return _passwordsViewModel.IsListPaneVisible &&
                   !_passwordsViewModel.IsMultiSelectionToolbarVisible;
        }

        if (_profileViewModel is null || !ReferenceEquals(CurrentPageViewModel, _profileViewModel))
            return false;

        if (_profileViewModel.IsDevicesMainPage)
        {
            return _profileViewModel.IsDeviceListPaneVisible &&
                   !_profileViewModel.IsAddDeviceDialogOpen &&
                   !_profileViewModel.IsDeviceDisconnectDialogOpen &&
                   !_profileViewModel.IsLocalSyncDialogOpen;
        }

        return _profileViewModel.IsProfileMainPage && _profileViewModel.IsProfileTabsPaneVisible;
    }

    private void SetCurrentMobileNavigationIndex(int index)
    {
        if (_currentMobileNavigationIndex == index)
            return;

        _currentMobileNavigationIndex = index;
        this.RaisePropertyChanged(nameof(CurrentMobileNavigationIndex));
        this.RaisePropertyChanged(nameof(MobileCurrentPageLabel));
        this.RaisePropertyChanged(nameof(MobilePageIndicatorText));
    }

    private void RaiseMobileSwipeEligibilityProperties()
    {
        this.RaisePropertyChanged(nameof(CanNavigateMobileMainPagesBySwipe));
        this.RaisePropertyChanged(nameof(IsAnimatedMobileMainPageSwipeEnabled));
        this.RaisePropertyChanged(nameof(IsCustomMobileMainPageSwipeEnabled));
    }

    private void RaiseMobileAnimationProperties()
    {
        this.RaisePropertyChanged(nameof(IsMobileMainPageAnimationEnabled));
        RaiseMobileSwipeEligibilityProperties();
    }

    private static string BuildSubtitle(UserProfileInfoResponse profile) =>
        string.IsNullOrWhiteSpace(profile.Username)
            ? profile.Email
            : $"@{profile.Username}";


    private static string BuildDisplayName(UserProfileInfoResponse profile)
    {
        var fullName = string.Join(
            " ",
            new[] { profile.LastName?.Trim(), profile.FirstName?.Trim() }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            return profile.Username;
        }

        return profile.Email;
    }

    bool IFrontendRefreshTarget.IsActive => !IsDisposed && !PlatformServices.LifetimeToken.IsCancellationRequested;

    Guid IFrontendRefreshTarget.CurrentAuthenticationToken => _authSessionRegistry.CurrentUserToken;

    Guid? IFrontendRefreshTarget.CurrentUserId => CurrentUserId;

    IReadOnlyCollection<FrontendRefreshPage> IFrontendRefreshTarget.GetVisiblePages() => GetVisiblePages();

    public IReadOnlyCollection<FrontendRefreshPage> GetVisiblePages()
    {
        if (IsDisposed)
            return Array.Empty<FrontendRefreshPage>();

        if (ReferenceEquals(CurrentPageViewModel, SettingsViewModel))
            return [FrontendRefreshPage.BackgroundSync];

        if (_changeProfileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel))
            return [FrontendRefreshPage.AuthenticationSessions];

        if (ReferenceEquals(CurrentPageViewModel, LoginViewModel))
        {
            return LoginViewModel.CanAutomaticallyRefreshEnrollment
                ? [FrontendRefreshPage.Enrollment]
                : Array.Empty<FrontendRefreshPage>();
        }

        if (_passwordsViewModel is not null && ReferenceEquals(CurrentPageViewModel, _passwordsViewModel))
            return [FrontendRefreshPage.Passwords];

        if (_profileViewModel is not null && ReferenceEquals(CurrentPageViewModel, _profileViewModel))
        {
            return ProfileViewModel.IsDevicesMainPage
                ? [FrontendRefreshPage.Devices, FrontendRefreshPage.LocalDevice]
                : [FrontendRefreshPage.UserProfile];
        }

        return Array.Empty<FrontendRefreshPage>();
    }

    bool IFrontendRefreshTarget.CanRefreshPage(FrontendRefreshPage page) => CanRefreshPage(page);

    public bool CanRefreshPage(FrontendRefreshPage page) => page switch
    {
        FrontendRefreshPage.Passwords =>
            IsAuthenticated && _passwordsViewModel is not null && _passwordsViewModel.CanAutomaticallyRefresh,
        FrontendRefreshPage.UserProfile =>
            IsAuthenticated && _profileViewModel is not null &&
            _profileViewModel.CanAutomaticallyRefreshUserProfile && !_isSettingRememberMe,
        FrontendRefreshPage.Devices =>
            IsAuthenticated && _profileViewModel is not null && _profileViewModel.CanAutomaticallyRefreshDevices,
        FrontendRefreshPage.LocalDevice =>
            IsAuthenticated && _profileViewModel is not null && _profileViewModel.CanAutomaticallyRefreshDevices,
        FrontendRefreshPage.Enrollment =>
            ReferenceEquals(CurrentPageViewModel, LoginViewModel) && LoginViewModel.CanAutomaticallyRefreshEnrollment,
        FrontendRefreshPage.AuthenticationSessions =>
            _changeProfileViewModel is not null &&
            ReferenceEquals(CurrentPageViewModel, _changeProfileViewModel) &&
            !_changeProfileViewModel.IsStartupSelection && !_changeProfileViewModel.IsBusy,
        FrontendRefreshPage.BackgroundSync =>
            ReferenceEquals(CurrentPageViewModel, SettingsViewModel) &&
            SettingsViewModel.CanAutomaticallyRefreshBackgroundSync,
        _ => false
    };

    Task<bool> IFrontendRefreshTarget.RefreshPageAsync(
        FrontendRefreshPage page,
        FrontendChangeScope scopes,
        Func<bool> isCurrent,
        CancellationToken cancellationToken) =>
        RefreshPageAutomaticallyAsync(page, scopes, isCurrent, cancellationToken);

    Task<bool> IFrontendRefreshTarget.RevalidateCurrentSessionAsync(
        Func<bool> isCurrent,
        CancellationToken cancellationToken) =>
        RevalidateCurrentSessionAutomaticallyAsync(isCurrent, cancellationToken);

    private async Task<bool> RefreshPageAutomaticallyAsync(
        FrontendRefreshPage page,
        FrontendChangeScope scopes,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent() || !CanRefreshPage(page))
            return false;

        switch (page)
        {
            case FrontendRefreshPage.Passwords:
                return await PasswordsViewModel.RefreshAutomaticallyAsync(isCurrent, cancellationToken);

            case FrontendRefreshPage.UserProfile:
                return await RefreshUserProfileAutomaticallyAsync(isCurrent, cancellationToken);

            case FrontendRefreshPage.Devices:
                // Both scopes use the Phase 13 cached endpoint; neither starts network probes.
                return await ProfileViewModel.RefreshDevicesAutomaticallyAsync(isCurrent, cancellationToken);

            case FrontendRefreshPage.LocalDevice:
            {
                var localDevice = await _endpoints.GetLocalDeviceInfoAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent() || !CanRefreshPage(page))
                    return false;
                ProfileViewModel.ApplyLocalDeviceProjection(localDevice);
                return true;
            }

            case FrontendRefreshPage.Enrollment:
                return await LoginViewModel.RefreshEnrollmentAutomaticallyAsync(isCurrent, cancellationToken);

            case FrontendRefreshPage.AuthenticationSessions:
                return _changeProfileViewModel is not null &&
                       await _changeProfileViewModel.RefreshAutomaticallyAsync(isCurrent, cancellationToken);

            case FrontendRefreshPage.BackgroundSync:
                return await SettingsViewModel.RefreshBackgroundSyncAutomaticallyAsync(isCurrent, cancellationToken);

            default:
                return false;
        }
    }

    private async Task<bool> RefreshUserProfileAutomaticallyAsync(
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        var token = _authSessionRegistry.CurrentUserToken;
        if (token == Guid.Empty)
            return false;

        var expectedUserId = CurrentUserId;
        var profile = await _endpoints.GetUserProfileInfoAsync(token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent() || !CanRefreshPage(FrontendRefreshPage.UserProfile) ||
            (expectedUserId is { } userId && profile.UId != userId))
        {
            return false;
        }

        ProfileViewModel.ApplyAutomaticProfileProjection(profile);
        SetSessionProfile(token, profile);
        CurrentUserDisplayName = BuildDisplayName(profile);
        CurrentUserSubtitle = BuildSubtitle(profile);
        ApplyRememberMeFromSession(profile.IsRememberMeEnabled);
        return true;
    }

    private async Task<bool> RevalidateCurrentSessionAutomaticallyAsync(
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        foreach (var token in _authSessionRegistry.ListTokens().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent())
                return false;
            await RevalidateSessionTokenAsync(token, cancellationToken);
            if (!isCurrent())
                return false;
        }
        return true;
    }

    private void HandleRefreshPageViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        var property = args.PropertyName;
        if ((sender is PasswordsViewModel && property == "CurrentPane") ||
            (sender is ProfileViewModel && property is ("CurrentMainPage" or "CurrentProfilePane" or "CurrentDevicePane")) ||
            (sender is LoginViewModel && property == nameof(LoginViewModel.CanAutomaticallyRefreshEnrollment)))
        {
            RefreshNavigationChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if ((sender is PasswordsViewModel && property is
                "IsPasswordMultiSelectionActive" or "IsCustomColorMultiSelectionActive" or
                "IsPasswordTagMultiSelectionActive" or "IsDeleteConfirmationOpen" or
                "IsCustomColorDeleteConfirmationOpen" or "IsPasswordTagDeleteConfirmationOpen" or
                "IsExportConfirmationOpen") ||
            (sender is ProfileViewModel && property is
                "IsDeviceDisconnectDialogOpen" or "IsLocalSyncDialogOpen" or "IsAddDeviceDialogOpen" or
                "IsAddingDevice" or "AreDevicesLoaded") ||
            (sender is ChangeProfileViewModel && property == "IsBusy") ||
            (sender is SettingsViewModel && property == nameof(SettingsViewModel.CanAutomaticallyRefreshBackgroundSync)))
        {
            RefreshPageStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NotifyRefreshAuthenticationChanged() =>
        RefreshAuthenticationChanged?.Invoke(this, EventArgs.Empty);
}
