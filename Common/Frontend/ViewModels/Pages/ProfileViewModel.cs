using PasswordManagerLocal.Common.Frontend.Helpers;
using PasswordManagerLocal.Common.Frontend.Security;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Devices;
using PasswordManagerLocal.Common.Contracts.Authentication;
using PasswordManagerLocal.Common.Contracts.Security;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using System.Collections.ObjectModel;
using ReactiveUI.Primitives;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public sealed class ProfileViewModel : ViewModelBase
{
    private const string MainProfilePage = "profile";
    private const string MainDevicesPage = "devices";
    private const string ProfileTabsPane = "tabs";
    private const string ProfilePersonalInfoPane = "personal-info";
    private const string ProfileUsernamePane = "username";
    private const string ProfilePasswordPane = "password";
    private const string ProfileDeleteAccountPane = "delete-account";
    private const string DeviceListPane = "list";
    private const string DeviceDetailsPane = "details";
    private const string DeviceAddPane = "add";
    private const string DeviceDisconnectPane = "disconnect";

    private static readonly string[] ProfileLocalizedPropertyNames =
    [
        nameof(Title),
        nameof(Subtitle),
        nameof(AccountOverviewLabel),
        nameof(BackToProfileLabel),
        nameof(OpenProfileSectionLabel),
        nameof(EditPersonalInfoLabel),
        nameof(EditPersonalInfoDescription),
        nameof(ChangeUsernameDescription),
        nameof(ChangeMasterPasswordDescription),
        nameof(DeleteAccountWarningTitle),
        nameof(DeleteAccountFinalWarning),
        nameof(OverviewTabLabel),
        nameof(DevicesTabLabel),
        nameof(AccountTabLabel),
        nameof(SecurityTabLabel),
        nameof(PersonalInfoTitle),
        nameof(UsernameTitle),
        nameof(SecurityTitle),
        nameof(DevicesTitle),
        nameof(DevicesDescription),
        nameof(DevicesEmptyTitle),
        nameof(DevicesEmptyDescription),
        nameof(DangerZoneTitle),
        nameof(UsernameLabel),
        nameof(FirstNameLabel),
        nameof(LastNameLabel),
        nameof(EmailLabel),
        nameof(RegistrationDateLabel),
        nameof(RegistrationDateInOriginalTimeZoneLabel),
        nameof(RegistrationTimeZoneLabel),
        nameof(RegistrationDeviceTypeLabel),
        nameof(RegistrationDateText),
        nameof(RegistrationDateInOriginalTimeZoneText),
        nameof(RegistrationTimeZoneText),
        nameof(RegistrationDeviceTypeText),
        nameof(SaveProfileLabel),
        nameof(ChangeUsernameLabel),
        nameof(ChangeMasterPasswordLabel),
        nameof(DeleteAccountLabel),
        nameof(CurrentPasswordLabel),
        nameof(NewPasswordLabel),
        nameof(ConfirmNewPasswordLabel),
        nameof(DeleteAccountDescription),
        nameof(EditUsernamePlaceholder),
        nameof(CurrentPasswordPlaceholder),
        nameof(NewPasswordPlaceholder),
        nameof(ConfirmNewPasswordPlaceholder),
        nameof(DeleteAccountPasswordPlaceholder),
        nameof(PasswordStrengthLabel),
        nameof(PasswordStrengthInfoTitle),
        nameof(PasswordStrengthInfoBody),
        nameof(PasswordStrengthInfoAccessibleLabel),
    ];

    private static readonly string[] DeviceLocalizedPropertyNames =
    [
        nameof(RefreshDevicesLabel),
        nameof(AddDeviceLabel),
        nameof(AddDeviceIconLabel),
        nameof(DeviceSearchLabel),
        nameof(DeviceSearchPlaceholder),
        nameof(DeviceSearchModeLabel),
        nameof(DeviceSearchModeNameLabel),
        nameof(DeviceSearchModeTypeLabel),
        nameof(DeviceSwitchOnLabel),
        nameof(DeviceSwitchOffLabel),
        nameof(DeviceSortLabel),
        nameof(DeviceSortNameAscMenuLabel),
        nameof(DeviceSortNameDescMenuLabel),
        nameof(DeviceSortStatusOnlineFirstMenuLabel),
        nameof(DeviceSortStatusIssuesFirstMenuLabel),
        nameof(DeviceSortLinkedNewestMenuLabel),
        nameof(DeviceSortLinkedOldestMenuLabel),
        nameof(DeviceSortLastSyncNewestMenuLabel),
        nameof(DeviceSortLastSyncOldestMenuLabel),
        nameof(DeviceSearchEmptyTitle),
        nameof(DeviceSearchEmptyDescription),
        nameof(BackToDevicesLabel),
        nameof(DeviceDetailsTitle),
        nameof(AddDeviceDialogTitle),
        nameof(AddDeviceDescription),
        nameof(AddDeviceCodeLabel),
        nameof(AddDeviceCodePlaceholder),
        nameof(AddDeviceQrOptionsDescription),
        nameof(ScanDeviceEnrollmentQrCodeLabel),
        nameof(PickDeviceEnrollmentQrImageLabel),
        nameof(PickDeviceEnrollmentQrImageTitle),
        nameof(ScanDeviceEnrollmentQrCodeTitle),
        nameof(ScanDeviceEnrollmentQrCodeDescription),
        nameof(ConfirmAddDeviceLabel),
        nameof(CurrentDeviceLabel),
        nameof(BlockedLabel),
        nameof(TrustedLabel),
        nameof(NotTrustedLabel),
        nameof(SyncEnabledLabel),
        nameof(SyncDisabledLabel),
        nameof(OnlineLabel),
        nameof(OfflineLabel),
        nameof(SyncToggleOnLabel),
        nameof(SyncToggleOffLabel),
        nameof(SaveDeviceNameLabel),
        nameof(UnblockDeviceLabel),
        nameof(DisconnectDeviceLabel),
        nameof(DeviceNameLabel),
        nameof(DeviceTypeLabel),
        nameof(WindowsPcDeviceTypeLabel),
        nameof(AndroidMobileDeviceTypeLabel),
        nameof(UnknownDeviceTypeLabel),
        nameof(DeviceLastLoginDateLabel),
        nameof(DevicePreviousLoginDateLabel),
        nameof(DeviceLastSyncLabel),
        nameof(CurrentDeviceLinkedAtLabel),
        nameof(RemoteDeviceLinkedAtLabel),
        nameof(DeviceBlockedReasonLabel),
        nameof(DeviceBlockedAtLabel),
        nameof(DeviceInvalidAttemptsLabel),
        nameof(DisconnectDialogTitle),
        nameof(DisconnectDialogWarning),
        nameof(DisconnectDialogPasswordPlaceholder),
        nameof(ConfirmDisconnectLabel),
        nameof(CancelLabel),
        nameof(LocalSyncDialogTitle),
        nameof(LocalSyncDialogWarning),
        nameof(LocalSyncConfirmLabel),
    ];

    private readonly IEndpoints _endpoints;
    private readonly PasswordStrengthEstimator _passwordStrengthEstimator = new();
    private readonly Func<Task<bool>> _refreshAuthenticatedStateAsync;
    private readonly Func<Task> _handleAccountDeletedAsync;
    private readonly List<DeviceItemViewModel> _allDevices = [];

    private Guid _token;
    private string _username = string.Empty;
    private string _firstName = string.Empty;
    private string _lastName = string.Empty;
    private string _email = string.Empty;
    private DateTime _registrationDate;
    private string _registrationTimeZoneId = string.Empty;
    private DeviceType _registrationDeviceType = DeviceType.Unknown;
    private string _editFirstName = string.Empty;
    private string _editLastName = string.Empty;
    private string _editEmail = string.Empty;
    private string _editUsername = string.Empty;
    private string _currentPassword = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmNewPassword = string.Empty;
    private string _deleteAccountPassword = string.Empty;
    private string _disconnectDevicePassword = string.Empty;
    private DeviceItemViewModel? _selectedDevice;
    private DeviceItemViewModel? _deviceToDisconnect;
    private DeviceItemViewModel? _pendingLocalSyncDevice;
    private bool _isDeviceDisconnectDialogOpen;
    private bool _isLocalSyncDialogOpen;
    private bool _isAddDeviceDialogOpen;
    private bool _pendingLocalSyncEnabled;
    private bool _isAddingDevice;
    private string _deviceEnrollmentCodeInput = string.Empty;
    private string _deviceSearchQuery = string.Empty;
    private bool _isDeviceSearchNameEnabled = true;
    private bool _isDeviceSearchTypeEnabled = true;
    private string _currentMainPage = MainProfilePage;
    private string _currentProfilePane = ProfileTabsPane;
    private string _currentDevicePane = DeviceListPane;
    private ProfilePaneTransitionViewModel? _currentAnimatedProfilePaneViewModel;
    private DevicePaneTransitionViewModel? _currentAnimatedDevicePaneViewModel;
    private bool _isProfilePaneTransitionReversed;
    private bool _isDevicePaneTransitionReversed;
    private int _selectedProfileTabIndex;
    private int _newPasswordStrength;
    private PasswordSortOptionViewModel? _selectedDeviceSortOption;

    public ProfileViewModel(
        UiPreferencesService uiPreferences,
        IEndpoints endpoints,
        Func<Task<bool>> refreshAuthenticatedStateAsync,
        Func<Task> handleAccountDeletedAsync)
        : base(uiPreferences)
    {
        _endpoints = endpoints;
        _refreshAuthenticatedStateAsync = refreshAuthenticatedStateAsync;
        _handleAccountDeletedAsync = handleAccountDeletedAsync;

        Devices = [];
        DeviceSortOptions = [];

        SaveProfileCommand = Own(ReactiveCommand.CreateFromTask(SaveProfileAsync));
        ChangeUsernameCommand = Own(ReactiveCommand.CreateFromTask(ChangeUsernameAsync));
        ChangeMasterPasswordCommand = Own(ReactiveCommand.CreateFromTask(ChangeMasterPasswordAsync));
        DeleteAccountCommand = Own(ReactiveCommand.CreateFromTask(DeleteAccountAsync));
        RefreshDevicesCommand = Own(ReactiveCommand.CreateFromTask(RefreshDevicesAsync));
        SearchDevicesCommand = Own(ReactiveCommand.Create(ApplyCurrentDeviceSearch));
        SelectDeviceSortOptionCommand = Own(ReactiveCommand.Create<string>(SelectDeviceSortOptionByKey));
        BackToDevicesCommand = Own(ReactiveCommand.Create(BackToDevices));
        ConfirmDisconnectDeviceCommand = Own(ReactiveCommand.CreateFromTask(ConfirmDisconnectDeviceAsync));
        CancelDisconnectDeviceCommand = Own(ReactiveCommand.Create(CancelDisconnectDevice));
        ConfirmLocalSyncToggleCommand = Own(ReactiveCommand.CreateFromTask(ConfirmLocalSyncToggleAsync));
        CancelLocalSyncToggleCommand = Own(ReactiveCommand.Create(CancelLocalSyncToggle));
        BeginAddDeviceCommand = Own(ReactiveCommand.CreateFromTask(BeginAddDeviceAsync));
        ConfirmAddDeviceCommand = Own(ReactiveCommand.CreateFromTask(ConfirmAddDeviceAsync));
        ScanDeviceEnrollmentQrCodeCommand = Own(ReactiveCommand.CreateFromTask(ScanDeviceEnrollmentQrCodeAsync));
        PickDeviceEnrollmentQrImageCommand = Own(ReactiveCommand.CreateFromTask(PickDeviceEnrollmentQrImageAsync));
        CancelAddDeviceCommand = Own(ReactiveCommand.Create(CancelAddDevice));
        BackToProfileCommand = Own(ReactiveCommand.Create(BackToProfile));
        BeginEditPersonalInfoCommand = Own(ReactiveCommand.Create(BeginEditPersonalInfo));
        BeginChangeUsernameCommand = Own(ReactiveCommand.Create(BeginChangeUsername));
        BeginChangeMasterPasswordCommand = Own(ReactiveCommand.Create(BeginChangeMasterPassword));
        BeginDeleteAccountCommand = Own(ReactiveCommand.Create(BeginDeleteAccount));
        RebuildDeviceSortOptions();
        SelectDefaultDeviceSortOption();
    }

    public string Username
    {
        get => _username;
        private set
        {
            this.RaiseAndSetIfChanged(ref _username, value);
            RefreshNewPasswordStrength();
        }
    }

    public string FirstName
    {
        get => _firstName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _firstName, value);
            RefreshNewPasswordStrength();
        }
    }

    public string LastName
    {
        get => _lastName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastName, value);
            RefreshNewPasswordStrength();
        }
    }

    public string Email
    {
        get => _email;
        private set
        {
            this.RaiseAndSetIfChanged(ref _email, value);
            RefreshNewPasswordStrength();
        }
    }

    public DateTime RegistrationDate
    {
        get => _registrationDate;
        private set
        {
            this.RaiseAndSetIfChanged(ref _registrationDate, value);
            this.RaisePropertyChanged(nameof(RegistrationDateText));
            this.RaisePropertyChanged(nameof(RegistrationDateInOriginalTimeZoneText));
            this.RaisePropertyChanged(nameof(ShowRegistrationDateInOriginalTimeZone));
        }
    }

    public string RegistrationTimeZoneId
    {
        get => _registrationTimeZoneId;
        private set
        {
            this.RaiseAndSetIfChanged(ref _registrationTimeZoneId, value);
            this.RaisePropertyChanged(nameof(RegistrationTimeZoneText));
            this.RaisePropertyChanged(nameof(RegistrationDateInOriginalTimeZoneText));
            this.RaisePropertyChanged(nameof(ShowRegistrationDateInOriginalTimeZone));
        }
    }

    public DeviceType RegistrationDeviceType
    {
        get => _registrationDeviceType;
        private set
        {
            this.RaiseAndSetIfChanged(ref _registrationDeviceType, value);
            this.RaisePropertyChanged(nameof(RegistrationDeviceTypeText));
        }
    }

    public string EditFirstName
    {
        get => _editFirstName;
        set => this.RaiseAndSetIfChanged(ref _editFirstName, value);
    }

    public string EditLastName
    {
        get => _editLastName;
        set => this.RaiseAndSetIfChanged(ref _editLastName, value);
    }

    public string EditEmail
    {
        get => _editEmail;
        set => this.RaiseAndSetIfChanged(ref _editEmail, value);
    }

    public string EditUsername
    {
        get => _editUsername;
        set => this.RaiseAndSetIfChanged(ref _editUsername, value);
    }

    public string CurrentPassword
    {
        get => _currentPassword;
        set => this.RaiseAndSetIfChanged(ref _currentPassword, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _newPassword, value);
            RefreshNewPasswordStrength();
        }
    }

    public int NewPasswordStrength => _newPasswordStrength;

    public string ConfirmNewPassword
    {
        get => _confirmNewPassword;
        set => this.RaiseAndSetIfChanged(ref _confirmNewPassword, value);
    }

    public string DeleteAccountPassword
    {
        get => _deleteAccountPassword;
        set => this.RaiseAndSetIfChanged(ref _deleteAccountPassword, value);
    }

    public string DisconnectDevicePassword
    {
        get => _disconnectDevicePassword;
        set => this.RaiseAndSetIfChanged(ref _disconnectDevicePassword, value);
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; }

    public event EventHandler? DeviceListScrollToTopRequested;

    public ObservableCollection<PasswordSortOptionViewModel> DeviceSortOptions { get; }

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedDevice, value);
            this.RaisePropertyChanged(nameof(HasSelectedDevice));
        }
    }

    public bool HasSelectedDevice => SelectedDevice is not null;

    public bool HasDevices => Devices.Count > 0;

    public bool IsDevicesEmpty => Devices.Count == 0;

    public bool HasKnownDevices => _allDevices.Count > 0;

    public bool IsDeviceListEmpty => _allDevices.Count == 0;

    public bool IsDeviceSearchResultEmpty => HasKnownDevices && Devices.Count == 0;

    public bool IsDeviceDisconnectDialogOpen
    {
        get => _isDeviceDisconnectDialogOpen;
        private set => this.RaiseAndSetIfChanged(ref _isDeviceDisconnectDialogOpen, value);
    }

    public bool IsLocalSyncDialogOpen
    {
        get => _isLocalSyncDialogOpen;
        private set => this.RaiseAndSetIfChanged(ref _isLocalSyncDialogOpen, value);
    }

    public bool IsAddDeviceDialogOpen
    {
        get => _isAddDeviceDialogOpen;
        private set => this.RaiseAndSetIfChanged(ref _isAddDeviceDialogOpen, value);
    }

    public bool IsAddingDevice
    {
        get => _isAddingDevice;
        private set => this.RaiseAndSetIfChanged(ref _isAddingDevice, value);
    }

    public string DeviceEnrollmentCodeInput
    {
        get => _deviceEnrollmentCodeInput;
        set => this.RaiseAndSetIfChanged(ref _deviceEnrollmentCodeInput, value);
    }

    public string DeviceSearchQuery
    {
        get => _deviceSearchQuery;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_deviceSearchQuery, value, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _deviceSearchQuery, value);
            ApplyDeviceFiltersAndSorting(SelectedDevice?.DeviceId, preserveSelection: true);
        }
    }

    public bool IsDeviceSearchNameEnabled
    {
        get => _isDeviceSearchNameEnabled;
        set => SetDeviceSearchMode(ref _isDeviceSearchNameEnabled, value, nameof(IsDeviceSearchNameEnabled));
    }

    public bool IsDeviceSearchTypeEnabled
    {
        get => _isDeviceSearchTypeEnabled;
        set => SetDeviceSearchMode(ref _isDeviceSearchTypeEnabled, value, nameof(IsDeviceSearchTypeEnabled));
    }

    public bool CanToggleDeviceSearchName => CanToggleDeviceSearchMode(_isDeviceSearchNameEnabled);

    public bool CanToggleDeviceSearchType => CanToggleDeviceSearchMode(_isDeviceSearchTypeEnabled);

    public PasswordSortOptionViewModel? SelectedDeviceSortOption
    {
        get => _selectedDeviceSortOption;
        set
        {
            if (ReferenceEquals(_selectedDeviceSortOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedDeviceSortOption, value);
            UpdateDeviceSortOptionSelectionMarks();
            RaiseDeviceSortMenuLabelProperties();
            ApplyDeviceFiltersAndSorting(SelectedDevice?.DeviceId, preserveSelection: true);
        }
    }


    public string CurrentMainPage
    {
        get => _currentMainPage;
        private set
        {
            if (string.Equals(_currentMainPage, value, StringComparison.Ordinal))
            {
                return;
            }

            ClearStatusMessage();
            this.RaiseAndSetIfChanged(ref _currentMainPage, value);
            this.RaisePropertyChanged(nameof(IsProfileMainPage));
            this.RaisePropertyChanged(nameof(IsDevicesMainPage));
        }
    }

    public bool IsProfileMainPage => CurrentMainPage == MainProfilePage;

    public bool IsDevicesMainPage => CurrentMainPage == MainDevicesPage;

    public int SelectedProfileTabIndex
    {
        get => _selectedProfileTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedProfileTabIndex, value);
    }

    public string CurrentProfilePane
    {
        get => _currentProfilePane;
        private set => SetCurrentProfilePane(value, false);
    }

    public ProfilePaneTransitionViewModel CurrentAnimatedProfilePaneViewModel
    {
        get => _currentAnimatedProfilePaneViewModel ??= CreateProfilePaneTransitionViewModel(CurrentProfilePane);
        private set => this.RaiseAndSetIfChanged(ref _currentAnimatedProfilePaneViewModel, value);
    }

    public bool IsProfilePaneTransitionReversed
    {
        get => _isProfilePaneTransitionReversed;
        private set => this.RaiseAndSetIfChanged(ref _isProfilePaneTransitionReversed, value);
    }

    public bool IsAndroidPaneTransitionEnabled => OperatingSystem.IsAndroid();

    public bool IsStaticPaneContentVisible => !IsAndroidPaneTransitionEnabled;

    public bool IsProfileTabsPaneVisible => CurrentProfilePane == ProfileTabsPane;

    public bool IsProfilePersonalInfoPaneVisible => CurrentProfilePane == ProfilePersonalInfoPane;

    public bool IsProfileUsernamePaneVisible => CurrentProfilePane == ProfileUsernamePane;

    public bool IsProfilePasswordPaneVisible => CurrentProfilePane == ProfilePasswordPane;

    public bool IsProfileDeleteAccountPaneVisible => CurrentProfilePane == ProfileDeleteAccountPane;

    private void SetCurrentProfilePane(string value, bool isBackNavigation)
    {
        if (string.Equals(_currentProfilePane, value, StringComparison.Ordinal))
        {
            return;
        }

        IsProfilePaneTransitionReversed = isBackNavigation;
        ClearStatusMessage();
        this.RaiseAndSetIfChanged(ref _currentProfilePane, value);
        this.RaisePropertyChanged(nameof(IsProfileTabsPaneVisible));
        this.RaisePropertyChanged(nameof(IsProfilePersonalInfoPaneVisible));
        this.RaisePropertyChanged(nameof(IsProfileUsernamePaneVisible));
        this.RaisePropertyChanged(nameof(IsProfilePasswordPaneVisible));
        this.RaisePropertyChanged(nameof(IsProfileDeleteAccountPaneVisible));
        CurrentAnimatedProfilePaneViewModel = CreateProfilePaneTransitionViewModel(value);
    }

    private ProfilePaneTransitionViewModel CreateProfilePaneTransitionViewModel(string pane) =>
        pane switch
        {
            ProfilePersonalInfoPane => new ProfilePersonalInfoPaneTransitionViewModel(this),
            ProfileUsernamePane => new ProfileUsernamePaneTransitionViewModel(this),
            ProfilePasswordPane => new ProfileMasterPasswordPaneTransitionViewModel(this),
            ProfileDeleteAccountPane => new ProfileDeleteAccountPaneTransitionViewModel(this),
            _ => new ProfileTabsPaneTransitionViewModel(this)
        };

    public string CurrentDevicePane
    {
        get => _currentDevicePane;
        private set => SetCurrentDevicePane(value, false);
    }

    public DevicePaneTransitionViewModel CurrentAnimatedDevicePaneViewModel
    {
        get => _currentAnimatedDevicePaneViewModel ??= CreateDevicePaneTransitionViewModel(CurrentDevicePane);
        private set => this.RaiseAndSetIfChanged(ref _currentAnimatedDevicePaneViewModel, value);
    }

    public bool IsDevicePaneTransitionReversed
    {
        get => _isDevicePaneTransitionReversed;
        private set => this.RaiseAndSetIfChanged(ref _isDevicePaneTransitionReversed, value);
    }

    public bool IsDeviceListPaneVisible => CurrentDevicePane == DeviceListPane;

    public bool IsDeviceDetailsPaneVisible => CurrentDevicePane == DeviceDetailsPane;

    public bool IsDeviceAddPaneVisible => CurrentDevicePane == DeviceAddPane;

    public bool IsDeviceDisconnectPaneVisible => CurrentDevicePane == DeviceDisconnectPane;

    public bool IsDeviceToolbarVisible => IsDeviceListPaneVisible;

    private void SetCurrentDevicePane(string value, bool isBackNavigation)
    {
        if (string.Equals(_currentDevicePane, value, StringComparison.Ordinal))
        {
            return;
        }

        IsDevicePaneTransitionReversed = isBackNavigation;
        ClearStatusMessage();
        this.RaiseAndSetIfChanged(ref _currentDevicePane, value);
        this.RaisePropertyChanged(nameof(IsDeviceListPaneVisible));
        this.RaisePropertyChanged(nameof(IsDeviceDetailsPaneVisible));
        this.RaisePropertyChanged(nameof(IsDeviceAddPaneVisible));
        this.RaisePropertyChanged(nameof(IsDeviceDisconnectPaneVisible));
        this.RaisePropertyChanged(nameof(IsDeviceToolbarVisible));
        CurrentAnimatedDevicePaneViewModel = CreateDevicePaneTransitionViewModel(value);
    }

    private DevicePaneTransitionViewModel CreateDevicePaneTransitionViewModel(string pane) =>
        pane switch
        {
            DeviceDetailsPane => new DeviceDetailsPaneTransitionViewModel(this),
            DeviceAddPane => new DeviceAddPaneTransitionViewModel(this),
            DeviceDisconnectPane => new DeviceDisconnectPaneTransitionViewModel(this),
            _ => new DeviceListPaneTransitionViewModel(this)
        };

    public DeviceItemViewModel? DeviceToDisconnect
    {
        get => _deviceToDisconnect;
        private set
        {
            this.RaiseAndSetIfChanged(ref _deviceToDisconnect, value);
            this.RaisePropertyChanged(nameof(DisconnectDeviceName));
        }
    }

    public DeviceItemViewModel? PendingLocalSyncDevice
    {
        get => _pendingLocalSyncDevice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pendingLocalSyncDevice, value);
            this.RaisePropertyChanged(nameof(LocalSyncDeviceName));
        }
    }

    public bool PendingLocalSyncEnabled
    {
        get => _pendingLocalSyncEnabled;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pendingLocalSyncEnabled, value);
            this.RaisePropertyChanged(nameof(LocalSyncConfirmLabel));
        }
    }

    public string DisconnectDeviceName => DeviceToDisconnect?.Name ?? string.Empty;

    public string LocalSyncDeviceName => PendingLocalSyncDevice?.Name ?? string.Empty;

    public string RegistrationDateText
    {
        get
        {
            var localDate = FrontendDateTimeUtil.ToLocalFromBackendUtc(RegistrationDate);
            return localDate == default ? string.Empty : $"{localDate:f} ({TimeZoneInfo.Local.Id})";
        }
    }

    public bool ShowRegistrationDateInOriginalTimeZone =>
        RegistrationDate != default
        && !string.IsNullOrWhiteSpace(RegistrationTimeZoneId)
        && !FrontendDateTimeUtil.IsCurrentTimeZone(RegistrationTimeZoneId, RegistrationDate);

    public string RegistrationDateInOriginalTimeZoneText
    {
        get
        {
            if (!ShowRegistrationDateInOriginalTimeZone)
                return string.Empty;

            var originalDate = FrontendDateTimeUtil.ToTimeZoneFromBackendUtc(RegistrationDate, RegistrationTimeZoneId);
            return $"{originalDate:f} ({RegistrationTimeZoneId})";
        }
    }

    public string RegistrationTimeZoneText =>
        FrontendDateTimeUtil.GetTimeZoneDisplayText(RegistrationTimeZoneId);

    public string RegistrationDeviceTypeText => RegistrationDeviceType switch
    {
        DeviceType.WindowsPc => WindowsPcDeviceTypeLabel,
        DeviceType.AndroidMobile => AndroidMobileDeviceTypeLabel,
        _ => UnknownDeviceTypeLabel
    };

    public ReactiveCommand<RxVoid, RxVoid> SaveProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ChangeUsernameCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ChangeMasterPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeleteAccountCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RefreshDevicesCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SearchDevicesCommand { get; }

    public ReactiveCommand<string, RxVoid> SelectDeviceSortOptionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackToDevicesCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmDisconnectDeviceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelDisconnectDeviceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmLocalSyncToggleCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelLocalSyncToggleCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginAddDeviceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConfirmAddDeviceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ScanDeviceEnrollmentQrCodeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> PickDeviceEnrollmentQrImageCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelAddDeviceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BackToProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginEditPersonalInfoCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginChangeUsernameCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginChangeMasterPasswordCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> BeginDeleteAccountCommand { get; }

    public string Title => GetTranslation("Profile_Title");

    public string Subtitle => GetTranslation("Profile_Subtitle");

    public string AccountOverviewLabel => GetTranslation("Profile_Overview_Title");

    public string BackToProfileLabel => GetTranslation("Profile_BackToProfile");

    public string OpenProfileSectionLabel => GetTranslation("Profile_OpenSection");

    public string EditPersonalInfoLabel => GetTranslation("Profile_EditPersonalInfo");

    public string EditPersonalInfoDescription => GetTranslation("Profile_EditPersonalInfo_Description");

    public string ChangeUsernameDescription => GetTranslation("Profile_ChangeUsername_Description");

    public string ChangeMasterPasswordDescription => GetTranslation("Profile_ChangeMasterPassword_Description");

    public string DeleteAccountWarningTitle => GetTranslation("Profile_DeleteAccount_Warning_Title");

    public string DeleteAccountFinalWarning => GetTranslation("Profile_DeleteAccount_FinalWarning");

    public string OverviewTabLabel => GetTranslation("Profile_Tab_Overview");

    public string DevicesTabLabel => GetTranslation("Profile_Tab_Devices");

    public string AccountTabLabel => GetTranslation("Profile_Tab_Account");

    public string SecurityTabLabel => GetTranslation("Profile_Tab_Security");

    public double ProfileTabHeaderFontSize => 13;

    public double ProfileTabHeaderMaxWidth => OperatingSystem.IsAndroid() ? 78 : 160;

    public string PersonalInfoTitle => GetTranslation("Profile_Personal_Title");

    public string UsernameTitle => GetTranslation("Profile_Username_Title");

    public string SecurityTitle => GetTranslation("Profile_Security_Title");

    public string DevicesTitle => GetTranslation("Profile_Devices_Title");

    public string DevicesDescription => GetTranslation("Profile_Devices_Description");

    public string DevicesEmptyTitle => GetTranslation("Profile_Devices_Empty_Title");

    public string DevicesEmptyDescription => GetTranslation("Profile_Devices_Empty_Description");

    public string DangerZoneTitle => GetTranslation("Profile_Danger_Title");

    public string UsernameLabel => GetTranslation("Login_Username_Label");

    public string FirstNameLabel => GetTranslation("Register_FirstName_Label");

    public string LastNameLabel => GetTranslation("Register_LastName_Label");

    public string EmailLabel => GetTranslation("Register_Email_Label");

    public string RegistrationDateLabel => GetTranslation("Profile_RegistrationDate");

    public string RegistrationDateInOriginalTimeZoneLabel => GetTranslation("Profile_RegistrationDate_OriginalTimeZone");

    public string RegistrationTimeZoneLabel => GetTranslation("Profile_RegistrationTimeZone");

    public string RegistrationDeviceTypeLabel => GetTranslation("Profile_RegistrationDeviceType");

    public string SaveProfileLabel => GetTranslation("Common_Save");

    public string ChangeUsernameLabel => GetTranslation("Profile_ChangeUsername");

    public string ChangeMasterPasswordLabel => GetTranslation("Profile_ChangeMasterPassword");

    public string DeleteAccountLabel => GetTranslation("Profile_DeleteAccount");

    public string CurrentPasswordLabel => GetTranslation("Profile_CurrentPassword");

    public string NewPasswordLabel => GetTranslation("Profile_NewPassword");

    public string ConfirmNewPasswordLabel => GetTranslation("Profile_ConfirmNewPassword");

    public string DeleteAccountDescription => GetTranslation("Profile_Danger_Description");

    public string EditUsernamePlaceholder => GetTranslation("Profile_Username_Placeholder");

    public string CurrentPasswordPlaceholder => GetTranslation("Profile_CurrentPassword_Placeholder");

    public string NewPasswordPlaceholder => GetTranslation("Profile_NewPassword_Placeholder");

    public string ConfirmNewPasswordPlaceholder => GetTranslation("Profile_ConfirmNewPassword_Placeholder");

    public string DeleteAccountPasswordPlaceholder => GetTranslation("Profile_DeletePassword_Placeholder");

    public string PasswordStrengthLabel => GetTranslation("PasswordStrength_Label");

    public string PasswordStrengthInfoTitle => GetTranslation("PasswordStrength_Info_Title");

    public string PasswordStrengthInfoBody => GetTranslation("PasswordStrength_Info_Body");

    public string PasswordStrengthInfoAccessibleLabel => GetTranslation("PasswordStrength_Info_AccessibleLabel");

    public string RefreshDevicesLabel => GetTranslation("Common_Refresh");

    public string AddDeviceLabel => GetTranslation("Profile_Device_Add");

    public string AddDeviceIconLabel => GetTranslation("Common_Add_Icon");

    public string DeviceSearchLabel => GetTranslation("Common_Search");

    public string DeviceSearchPlaceholder => GetTranslation("Profile_Device_Search_Placeholder");

    public string DeviceSearchModeLabel => GetTranslation("Profile_Device_SearchMode_Label");

    public string DeviceSearchModeNameLabel => GetTranslation("Common_Name");

    public string DeviceSearchModeTypeLabel => GetTranslation("Profile_Device_Type");

    public string DeviceSwitchOnLabel => GetTranslation("Common_On");

    public string DeviceSwitchOffLabel => GetTranslation("Common_Off");

    public string DeviceSortLabel => GetTranslation("Passwords_Sort_Label");

    public string DeviceSortNameAscMenuLabel => BuildDeviceSortMenuLabel("name-asc", "Passwords_Sort_NameAsc");

    public string DeviceSortNameDescMenuLabel => BuildDeviceSortMenuLabel("name-desc", "Passwords_Sort_NameDesc");

    public string DeviceSortStatusOnlineFirstMenuLabel =>
        BuildDeviceSortMenuLabel("status-online-first", "Profile_Device_Sort_StatusOnlineFirst");

    public string DeviceSortStatusIssuesFirstMenuLabel =>
        BuildDeviceSortMenuLabel("status-issues-first", "Profile_Device_Sort_StatusIssuesFirst");

    public string DeviceSortLinkedNewestMenuLabel =>
        BuildDeviceSortMenuLabel("linked-desc", "Profile_Device_Sort_LinkedNewest");

    public string DeviceSortLinkedOldestMenuLabel =>
        BuildDeviceSortMenuLabel("linked-asc", "Profile_Device_Sort_LinkedOldest");

    public string DeviceSortLastSyncNewestMenuLabel =>
        BuildDeviceSortMenuLabel("last-sync-desc", "Profile_Device_Sort_LastSyncNewest");

    public string DeviceSortLastSyncOldestMenuLabel =>
        BuildDeviceSortMenuLabel("last-sync-asc", "Profile_Device_Sort_LastSyncOldest");

    public string DeviceSearchEmptyTitle => GetTranslation("Profile_Device_SearchEmpty_Title");

    public string DeviceSearchEmptyDescription => GetTranslation("Profile_Device_SearchEmpty_Description");

    public string BackToDevicesLabel => GetTranslation("Profile_Device_BackToDevices");

    public string DeviceDetailsTitle => GetTranslation("Profile_Device_Details_Title");

    public string AddDeviceDialogTitle => GetTranslation("Profile_Device_Add_Title");

    public string AddDeviceDescription => GetTranslation("Profile_Device_Add_Description");

    public string AddDeviceCodeLabel => GetTranslation("Profile_Device_Add_Code_Label");

    public string AddDeviceCodePlaceholder => GetTranslation("Profile_Device_Add_Code_Placeholder");

    public string AddDeviceQrOptionsDescription => GetTranslation("Profile_Device_Add_QrOptions_Description");

    public string ScanDeviceEnrollmentQrCodeLabel => GetTranslation("Profile_Device_Add_QrScan");

    public string PickDeviceEnrollmentQrImageLabel => GetTranslation("Profile_Device_Add_QrUpload");

    public string PickDeviceEnrollmentQrImageTitle => GetTranslation("Profile_Device_Add_QrUpload_Title");

    public string ScanDeviceEnrollmentQrCodeTitle => GetTranslation("Profile_Device_Add_QrScanner_Title");

    public string ScanDeviceEnrollmentQrCodeDescription => GetTranslation("Profile_Device_Add_QrScanner_Description");

    public bool IsDeviceEnrollmentCameraScanAvailable => PlatformServices.CameraScanner.IsAvailable;

    public string ConfirmAddDeviceLabel => GetTranslation("Profile_Device_Add_Confirm");

    public string CurrentDeviceLabel => GetTranslation("Profile_Device_Current");

    public string BlockedLabel => GetTranslation("Profile_Device_Blocked");

    public string TrustedLabel => GetTranslation("Profile_Device_Trusted");

    public string NotTrustedLabel => GetTranslation("Profile_Device_NotTrusted");

    public string SyncEnabledLabel => GetTranslation("Profile_Device_SyncEnabled");

    public string SyncDisabledLabel => GetTranslation("Profile_Device_SyncDisabled");

    public string OnlineLabel => GetTranslation("Profile_Device_Online");

    public string OfflineLabel => GetTranslation("Profile_Device_Offline");

    public string SyncToggleOnLabel => GetTranslation("Common_On");

    public string SyncToggleOffLabel => GetTranslation("Common_Off");

    public string SaveDeviceNameLabel => GetTranslation("Profile_Device_SaveName");

    public string UnblockDeviceLabel => GetTranslation("Profile_Device_Unblock");

    public string DisconnectDeviceLabel => GetTranslation("Profile_Device_Disconnect");

    public string DeviceNameLabel => GetTranslation("Profile_Device_Name");

    public string DeviceTypeLabel => GetTranslation("Profile_Device_Type");

    public string WindowsPcDeviceTypeLabel => GetTranslation("Profile_Device_Type_WindowsPc");

    public string AndroidMobileDeviceTypeLabel => GetTranslation("Profile_Device_Type_AndroidMobile");

    public string UnknownDeviceTypeLabel => GetTranslation("Profile_Device_Type_Unknown");

    public string DeviceLastLoginDateLabel => GetTranslation("Profile_LastLoginDate");

    public string DevicePreviousLoginDateLabel => GetTranslation("Profile_PreviousLoginDate");

    public string DeviceLastSyncLabel => GetTranslation("Profile_Device_LastSync");

    public string CurrentDeviceLinkedAtLabel => GetTranslation("Profile_Device_CurrentLinkedAt");

    public string RemoteDeviceLinkedAtLabel => GetTranslation("Profile_Device_RemoteLinkedAt");

    public string DeviceBlockedReasonLabel => GetTranslation("Profile_Device_BlockedReason");

    public string DeviceBlockedAtLabel => GetTranslation("Profile_Device_BlockedAt");

    public string DeviceInvalidAttemptsLabel => GetTranslation("Profile_Device_InvalidAttempts");

    public string DisconnectDialogTitle => GetTranslation("Profile_Device_Disconnect_Title");

    public string DisconnectDialogWarning => GetTranslation("Profile_Device_Disconnect_Warning");

    public string DisconnectDialogPasswordPlaceholder => GetTranslation("Profile_Device_Disconnect_Password_Placeholder");

    public string ConfirmDisconnectLabel => GetTranslation("Profile_Device_Disconnect_Confirm");

    public string CancelLabel => GetTranslation("Common_Cancel");

    public string LocalSyncDialogTitle => GetTranslation("Profile_LocalSync_Title");

    public string LocalSyncDialogWarning => GetTranslation("Profile_LocalSync_Warning");

    public string LocalSyncConfirmLabel => PendingLocalSyncEnabled
        ? GetTranslation("Profile_LocalSync_TurnOn")
        : GetTranslation("Profile_LocalSync_TurnOff");

    protected override void OnLanguageChanged()
    {
        RaisePropertiesChanged(ProfileLocalizedPropertyNames);
        RaisePropertiesChanged(DeviceLocalizedPropertyNames);
        ApplyLocalizationToDeviceItems();
        RebuildLocalizedDeviceSortOptions();
        ApplyDeviceFiltersAndSorting(SelectedDevice?.DeviceId, preserveSelection: true);
    }


    private void RebuildLocalizedDeviceSortOptions()
    {
        var selectedDeviceSortKey = SelectedDeviceSortOption?.Key;
        RebuildDeviceSortOptions();
        SelectedDeviceSortOption = DeviceSortOptions.FirstOrDefault(item => item.Key == selectedDeviceSortKey)
            ?? DeviceSortOptions.FirstOrDefault();
        UpdateDeviceSortOptionSelectionMarks();
        RaiseDeviceSortMenuLabelProperties();
    }

    public void ShowProfileMainPage()
    {
        DiscardTransientNavigationState();
        CurrentMainPage = MainProfilePage;
    }

    public void ShowDevicesMainPage()
    {
        DiscardTransientNavigationState();
        CurrentMainPage = MainDevicesPage;
        _ = RefreshDevicesAfterPageOpenAsync();
    }

    private async Task RefreshDevicesAfterPageOpenAsync()
    {
        if (_token != Guid.Empty)
            await LoadDevicesAsync();
    }

    public void RequestDeviceListScrollToTop() => DeviceListScrollToTopRequested?.Invoke(this, EventArgs.Empty);

    public void DiscardTransientNavigationState()
    {
        ClearStatusMessage();
        DeviceSearchQuery = string.Empty;
        ResetProfileEditFields();
        ResetDeviceNavigationState();
        SelectedProfileTabIndex = 0;
        CurrentProfilePane = ProfileTabsPane;
    }

    private void ResetProfileEditFields()
    {
        EditUsername = Username;
        EditFirstName = FirstName;
        EditLastName = LastName;
        EditEmail = Email;
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        DeleteAccountPassword = string.Empty;
    }

    private void ResetDeviceNavigationState()
    {
        SelectedDevice = null;
        DeviceToDisconnect = null;
        PendingLocalSyncDevice = null;
        PendingLocalSyncEnabled = false;
        IsDeviceDisconnectDialogOpen = false;
        IsLocalSyncDialogOpen = false;
        IsAddDeviceDialogOpen = false;
        IsAddingDevice = false;
        DisconnectDevicePassword = string.Empty;
        DeviceEnrollmentCodeInput = string.Empty;
        CurrentDevicePane = DeviceListPane;
    }

    internal bool HasConfirmableDialogOpen =>
        IsLocalSyncDialogOpen || IsDeviceDisconnectDialogOpen;

    internal async Task ConfirmOpenDialogAsync()
    {
        if (IsLocalSyncDialogOpen)
        {
            await ConfirmLocalSyncToggleAsync();
            return;
        }

        if (IsDeviceDisconnectDialogOpen)
            await ConfirmDisconnectDeviceAsync();
    }


    public bool TryNavigateBack()
    {
        if (IsLocalSyncDialogOpen)
        {
            CancelLocalSyncToggle();
            return true;
        }

        if (IsDeviceDisconnectDialogOpen)
        {
            CancelDisconnectDevice();
            return true;
        }

        if (IsDevicesMainPage && !IsDeviceListPaneVisible)
        {
            BackToDevices();
            return true;
        }

        if (IsProfileMainPage && !IsProfileTabsPaneVisible)
        {
            BackToProfile();
            return true;
        }

        return false;
    }


    public async Task RefreshDevicesOnlyAsync()
    {
        if (_token == Guid.Empty)
            return;

        ClearStatusMessage();
        if (await LoadDevicesAsync())
            ShowSuccessMessage(GetTranslation("Shell_DataRefreshed"));
    }

    public async Task RefreshCurrentDataAsync()
    {
        if (_token == Guid.Empty)
            return;

        try
        {
            ClearStatusMessage();
            var profile = await _endpoints.GetUserProfileInfoAsync(_token);
            if (await LoadAsync(_token, profile))
                ShowSuccessMessage(GetTranslation("Shell_DataRefreshed"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    public async Task<bool> LoadAsync(Guid token, UserProfileInfoResponse profile)
    {
        _token = token;
        Username = profile.Username;
        FirstName = profile.FirstName;
        LastName = profile.LastName;
        Email = profile.Email;
        RegistrationDate = profile.RegistrationDate;
        RegistrationTimeZoneId = profile.RegistrationTimeZoneId;
        RegistrationDeviceType = profile.RegistrationDeviceType;
        EditUsername = profile.Username;
        EditFirstName = profile.FirstName;
        EditLastName = profile.LastName;
        EditEmail = profile.Email;
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        DeleteAccountPassword = string.Empty;
        DisconnectDevicePassword = string.Empty;
        ClearStatusMessage();
        CancelDisconnectDevice();
        CancelLocalSyncToggle();
        CancelAddDevice();
        CurrentProfilePane = ProfileTabsPane;
        CurrentDevicePane = DeviceListPane;
        return await LoadDevicesAsync();
    }

    public void SetSessionToken(Guid token) => _token = token;

    public void Reset()
    {
        _token = Guid.Empty;
        Username = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
        Email = string.Empty;
        RegistrationDate = DateTime.MinValue;
        RegistrationTimeZoneId = string.Empty;
        RegistrationDeviceType = DeviceType.Unknown;
        EditUsername = string.Empty;
        EditFirstName = string.Empty;
        EditLastName = string.Empty;
        EditEmail = string.Empty;
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        DeleteAccountPassword = string.Empty;
        DisconnectDevicePassword = string.Empty;
        ClearStatusMessage();
        foreach (var device in _allDevices)
            device.Dispose();
        _allDevices.Clear();
        Devices.Clear();
        SelectedDevice = null;
        DeviceSearchQuery = string.Empty;
        CurrentMainPage = MainProfilePage;
        SelectedProfileTabIndex = 0;
        CurrentProfilePane = ProfileTabsPane;
        CurrentDevicePane = DeviceListPane;
        SelectDefaultDeviceSortOption();
        RaiseDeviceCollectionStateChanged();
        CancelDisconnectDevice();
        CancelLocalSyncToggle();
        CancelAddDevice();
    }

    private async Task SaveProfileAsync()
    {
        if (_token == Guid.Empty)
        {
            return;
        }

        ClearStatusMessage();

        try
        {
            await _endpoints.UpdateUserProfileInfoAsync(new UpdateUserProfileRequest
            {
                Token = _token,
                NewEamil = EditEmail.Trim(),
                newFirstName = EditFirstName.Trim(),
                NewLastName = EditLastName.Trim()
            });

            if (!await _refreshAuthenticatedStateAsync())
                return;

            CurrentProfilePane = ProfileTabsPane;
            ShowSuccessMessage(GetTranslation("Profile_Save_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private async Task ChangeUsernameAsync()
    {
        if (_token == Guid.Empty)
        {
            return;
        }

        ClearStatusMessage();

        try
        {
            await _endpoints.ChangeUsernameAsync(_token, EditUsername.Trim());
            if (!await _refreshAuthenticatedStateAsync())
                return;

            CurrentProfilePane = ProfileTabsPane;
            ShowSuccessMessage(GetTranslation("Profile_Username_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private void RefreshNewPasswordStrength()
    {
        var result = _passwordStrengthEstimator.Evaluate(
            NewPassword,
            [Username, FirstName, LastName, Email]);

        if (_newPasswordStrength == result.Score)
            return;

        _newPasswordStrength = result.Score;
        this.RaisePropertyChanged(nameof(NewPasswordStrength));
    }

    private async Task ChangeMasterPasswordAsync()
    {
        if (_token == Guid.Empty)
        {
            return;
        }

        ClearStatusMessage();

        if (!string.Equals(NewPassword, ConfirmNewPassword, StringComparison.Ordinal))
        {
            ShowErrorMessage(GetTranslation("Validation_RegisterPassword_Mismatch"));
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentPassword) || string.IsNullOrWhiteSpace(NewPassword))
        {
            ShowErrorMessage(GetTranslation("Validation_Password_Required"));
            return;
        }

        var currentPasswordHash = SecretTransform.HashPassword(CurrentPassword);
        var newPasswordHash = SecretTransform.HashPassword(NewPassword);

        try
        {
            await _endpoints.ChangeMasterPasswordAsync(new MasterPasswordChangeRequest
            {
                Token = _token,
                Password = currentPasswordHash,
                NewPassword = newPasswordHash
            });

            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;
            CurrentProfilePane = ProfileTabsPane;
            ShowSuccessMessage(GetTranslation("Profile_MasterPassword_Success"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentPasswordHash);
            CryptographicOperations.ZeroMemory(newPasswordHash);
        }
    }

    private async Task DeleteAccountAsync()
    {
        if (_token == Guid.Empty)
        {
            return;
        }

        ClearStatusMessage();

        if (string.IsNullOrWhiteSpace(DeleteAccountPassword))
        {
            ShowErrorMessage(GetTranslation("Validation_Password_Required"));
            return;
        }

        var passwordHash = SecretTransform.HashPassword(DeleteAccountPassword);

        try
        {
            await _endpoints.DeleteUserAccountAsync(_token, passwordHash);
            DeleteAccountPassword = string.Empty;
            await _handleAccountDeletedAsync();
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordHash);
        }
    }

    private void BackToProfile()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        DeleteAccountPassword = string.Empty;
        EditFirstName = FirstName;
        EditLastName = LastName;
        EditEmail = Email;
        EditUsername = Username;
        ClearStatusMessage();
        SetCurrentProfilePane(ProfileTabsPane, true);
    }

    private void BeginEditPersonalInfo()
    {
        EditFirstName = FirstName;
        EditLastName = LastName;
        EditEmail = Email;
        ClearStatusMessage();
        CurrentProfilePane = ProfilePersonalInfoPane;
    }

    private void BeginChangeUsername()
    {
        EditUsername = Username;
        ClearStatusMessage();
        CurrentProfilePane = ProfileUsernamePane;
    }

    private void BeginChangeMasterPassword()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        ClearStatusMessage();
        CurrentProfilePane = ProfilePasswordPane;
    }

    private void BeginDeleteAccount()
    {
        DeleteAccountPassword = string.Empty;
        ClearStatusMessage();
        CurrentProfilePane = ProfileDeleteAccountPane;
    }

    private async Task RefreshDevicesAsync()
    {
        ClearStatusMessage();
        if (await LoadDevicesAsync())
            ShowSuccessMessage(GetTranslation("Shell_DataRefreshed"));
    }

    private async Task<bool> LoadDevicesAsync()
    {
        if (_token == Guid.Empty)
            return false;

        var selectedId = SelectedDevice?.DeviceId;

        try
        {
            var devices = await _endpoints.GetUserDevicesAsync(_token);
            foreach (var device in _allDevices)
            device.Dispose();
        _allDevices.Clear();

            foreach (var device in devices)
                _allDevices.Add(CreateDeviceItem(device));

            ApplyDeviceFiltersAndSorting(selectedId, preserveSelection: selectedId.HasValue);
            return true;
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
            return false;
        }
    }

    private DeviceItemViewModel CreateDeviceItem(UserDeviceInfoResponse device) =>
        DeviceItemViewModel.Create(
            device,
            CreateDeviceItemLocalization(),
            BeginViewDeviceAsync,
            SaveDeviceNameAsync,
            ToggleDeviceSyncAsync,
            UnblockDeviceAsync,
            BeginDisconnectDevice);

    private DeviceItemLocalization CreateDeviceItemLocalization() =>
        new()
        {
            CurrentDeviceLabel = CurrentDeviceLabel,
            BlockedLabel = BlockedLabel,
            TrustedLabel = TrustedLabel,
            NotTrustedLabel = NotTrustedLabel,
            SyncEnabledLabel = SyncEnabledLabel,
            SyncDisabledLabel = SyncDisabledLabel,
            OnlineLabel = OnlineLabel,
            OfflineLabel = OfflineLabel,
            SyncToggleOnLabel = SyncToggleOnLabel,
            SyncToggleOffLabel = SyncToggleOffLabel,
            WindowsPcLabel = WindowsPcDeviceTypeLabel,
            AndroidMobileLabel = AndroidMobileDeviceTypeLabel,
            UnknownDeviceTypeLabel = UnknownDeviceTypeLabel,
            SaveNameLabel = SaveDeviceNameLabel,
            UnblockLabel = UnblockDeviceLabel,
            DisconnectLabel = DisconnectDeviceLabel,
            DeviceNameLabel = DeviceNameLabel,
            DeviceLastLoginDateLabel = DeviceLastLoginDateLabel,
            DevicePreviousLoginDateLabel = DevicePreviousLoginDateLabel,
            DeviceLastSyncLabel = DeviceLastSyncLabel,
            CurrentDeviceLinkedAtLabel = CurrentDeviceLinkedAtLabel,
            RemoteDeviceLinkedAtLabel = RemoteDeviceLinkedAtLabel,
            DeviceBlockedReasonLabel = DeviceBlockedReasonLabel,
            DeviceBlockedAtLabel = DeviceBlockedAtLabel,
            DeviceInvalidAttemptsLabel = DeviceInvalidAttemptsLabel
        };

    private Task BeginViewDeviceAsync(DeviceItemViewModel device)
    {
        SelectedDevice = device;
        ClearStatusMessage();
        CurrentDevicePane = DeviceDetailsPane;
        return Task.CompletedTask;
    }

    private void BackToDevices()
    {
        DeviceToDisconnect = null;
        DisconnectDevicePassword = string.Empty;
        DeviceEnrollmentCodeInput = string.Empty;
        IsAddingDevice = false;
        ClearStatusMessage();
        SetCurrentDevicePane(DeviceListPane, true);
    }

    private void ApplyCurrentDeviceSearch() =>
        ApplyDeviceFiltersAndSorting(SelectedDevice?.DeviceId, preserveSelection: true);

    private async Task SaveDeviceNameAsync(DeviceItemViewModel device)
    {
        if (_token == Guid.Empty)
            return;

        ClearStatusMessage();

        var normalizedName = device.EditableName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            ShowErrorMessage(GetTranslation("Profile_Device_NameRequired"));
            return;
        }

        try
        {
            if (device.IsCurrentDevice)
                await _endpoints.SetLocalDeviceNameAsync(_token, normalizedName);
            else
                await _endpoints.SetUserDeviceNameAsync(_token, device.DeviceId, normalizedName);

            device.ApplySavedName(normalizedName);
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(GetTranslation("Profile_Device_NameSaved"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private async Task ToggleDeviceSyncAsync(DeviceItemViewModel device)
    {
        if (_token == Guid.Empty)
            return;

        ClearStatusMessage();

        var targetState = !device.IsSyncOn;

        if (device.IsCurrentDevice)
        {
            PendingLocalSyncDevice = device;
            PendingLocalSyncEnabled = targetState;
            IsLocalSyncDialogOpen = true;
            return;
        }

        try
        {
            await _endpoints.SetUserDeviceSyncOnAsync(_token, device.DeviceId, targetState);
            device.ApplySyncState(targetState);
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(targetState
                ? GetTranslation("Profile_Device_SyncTurnedOn")
                : GetTranslation("Profile_Device_SyncTurnedOff"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private async Task UnblockDeviceAsync(DeviceItemViewModel device)
    {
        if (_token == Guid.Empty)
            return;

        ClearStatusMessage();

        try
        {
            await _endpoints.UnblockUserDeviceAsync(_token, device.DeviceId);
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(GetTranslation("Profile_Device_Unblocked"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private void BeginDisconnectDevice(DeviceItemViewModel device)
    {
        if (!device.CanDisconnect)
            return;

        SelectedDevice = device;
        DeviceToDisconnect = device;
        DisconnectDevicePassword = string.Empty;
        IsDeviceDisconnectDialogOpen = false;
        ClearStatusMessage();
        CurrentDevicePane = DeviceDisconnectPane;
    }

    private void CancelDisconnectDevice()
    {
        ClearStatusMessage();
        IsDeviceDisconnectDialogOpen = false;
        DeviceToDisconnect = null;
        DisconnectDevicePassword = string.Empty;

        if (IsDeviceDisconnectPaneVisible)
            SetCurrentDevicePane(SelectedDevice is null ? DeviceListPane : DeviceDetailsPane, true);
    }

    private async Task ConfirmDisconnectDeviceAsync()
    {
        if (_token == Guid.Empty || DeviceToDisconnect is null)
            return;

        ClearStatusMessage();

        if (string.IsNullOrWhiteSpace(DisconnectDevicePassword))
        {
            ShowErrorMessage(GetTranslation("Validation_Password_Required"));
            return;
        }

        var passwordHash = SecretTransform.HashPassword(DisconnectDevicePassword);

        try
        {
            await _endpoints.DisconnectUserDeviceAsync(_token, DeviceToDisconnect.DeviceId, passwordHash);
            CancelDisconnectDevice();
            SelectedDevice = null;
            CurrentDevicePane = DeviceListPane;
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(GetTranslation("Profile_Device_Disconnected"));
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordHash);
        }
    }

    private void CancelLocalSyncToggle()
    {
        ClearStatusMessage();
        IsLocalSyncDialogOpen = false;
        PendingLocalSyncDevice = null;
        PendingLocalSyncEnabled = false;
    }

    private async Task ConfirmLocalSyncToggleAsync()
    {
        if (PendingLocalSyncDevice is null)
            return;

        ClearStatusMessage();

        var targetState = PendingLocalSyncEnabled;

        try
        {
            if (targetState && !await FirewallPermissionStartupPrompt.EnsureConfiguredAsync(CurrentLanguage))
            {
                ShowErrorMessage(GetTranslation("Firewall_RequiredForLocalNetwork"));
                return;
            }

            await _endpoints.SetLocalUserSyncOnAsync(_token, targetState);
            PendingLocalSyncDevice.ApplySyncState(targetState);
            CancelLocalSyncToggle();
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(targetState
                ? GetTranslation("Profile_LocalSync_OnSuccess")
                : GetTranslation("Profile_LocalSync_OffSuccess"));
        }
        catch (Exception ex)
        {
            await FirewallPermissionStartupPrompt.RevalidateAfterLikelyFirewallFailureAsync(
                ex,
                CurrentLanguage);
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private async Task BeginAddDeviceAsync()
    {
        if (_token == Guid.Empty)
            return;

        ClearStatusMessage();

        try
        {
            var isLocalSyncOn = await _endpoints.GetLocalUserSyncOnAsync(_token);
            if (!isLocalSyncOn)
            {
                ShowErrorMessage(GetTranslation("Profile_Device_AddSyncDisabled"));
                return;
            }

            if (!await FirewallPermissionStartupPrompt.EnsureConfiguredAsync(CurrentLanguage))
            {
                ShowErrorMessage(GetTranslation("Firewall_RequiredForLocalNetwork"));
                return;
            }

            SelectedDevice = null;
            DeviceEnrollmentCodeInput = string.Empty;
            IsAddDeviceDialogOpen = false;
            ClearStatusMessage();
            CurrentDevicePane = DeviceAddPane;
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
    }

    private void CancelAddDevice()
    {
        ClearStatusMessage();
        IsAddDeviceDialogOpen = false;
        DeviceEnrollmentCodeInput = string.Empty;
        IsAddingDevice = false;

        if (IsDeviceAddPaneVisible)
            SetCurrentDevicePane(DeviceListPane, true);
    }

    private async Task ScanDeviceEnrollmentQrCodeAsync()
    {
        if (_token == Guid.Empty || IsAddingDevice)
            return;

        ClearStatusMessage();

        if (!PlatformServices.CameraScanner.IsAvailable)
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_QrCameraUnavailable"));
            return;
        }

        try
        {
            var code = await PlatformServices.CameraScanner.ScanEnrollmentCodeAsync(
                ScanDeviceEnrollmentQrCodeTitle,
                ScanDeviceEnrollmentQrCodeDescription, LifetimeToken);

            if (string.IsNullOrWhiteSpace(code))
                return;

            await AddDeviceFromQrTextAsync(code);
        }
        catch
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_QrDecodeFailed"));
        }
    }



    private async Task PickDeviceEnrollmentQrImageAsync()
    {
        if (_token == Guid.Empty || IsAddingDevice)
            return;

        try
        {
            ClearStatusMessage();
            var imageBytes = await PlatformServices.ImagePicker.PickImageBytesAsync(PickDeviceEnrollmentQrImageTitle, LifetimeToken);
            if (imageBytes is null || imageBytes.Length == 0)
                return;

            await AddDeviceFromQrImageBytesAsync(imageBytes);
        }
        catch
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_QrDecodeFailed"));
        }
    }



    private async Task AddDeviceFromQrImageBytesAsync(byte[] imageBytes)
    {
        var code = EnrollmentQrCodeService.DecodeEnrollmentCodeFromQrImage(imageBytes);
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_QrNotFound"));
            return;
        }

        await AddDeviceFromQrTextAsync(code);
    }



    private async Task AddDeviceFromQrTextAsync(string qrText)
    {
        var code = EnrollmentQrCodeService.ExtractEnrollmentCode(qrText);
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_QrNotFound"));
            return;
        }

        DeviceEnrollmentCodeInput = code.Trim();
        await ConfirmAddDeviceAsync();
    }



    private async Task ConfirmAddDeviceAsync()
    {
        if (_token == Guid.Empty || IsAddingDevice)
            return;

        ClearStatusMessage();

        var code = DeviceEnrollmentCodeInput.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowErrorMessage(GetTranslation("Profile_Device_Add_CodeRequired"));
            return;
        }

        try
        {
            IsAddingDevice = true;
            if (!await FirewallPermissionStartupPrompt.EnsureConfiguredAsync(CurrentLanguage))
            {
                ShowErrorMessage(GetTranslation("Firewall_RequiredForLocalNetwork"));
                return;
            }

            await _endpoints.AddDeviceByCodeAsync(_token, code);
            CancelAddDevice();
            CurrentDevicePane = DeviceListPane;
            if (!await LoadDevicesAsync())
                return;

            ShowSuccessMessage(GetTranslation("Profile_Device_AddSuccess"));
        }
        catch (Exception ex)
        {
            await FirewallPermissionStartupPrompt.RevalidateAfterLikelyFirewallFailureAsync(
                ex,
                CurrentLanguage);
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            IsAddingDevice = false;
        }
    }


    private void ApplyDeviceFiltersAndSorting(Guid? preferredSelectionId, bool preserveSelection)
    {
        IEnumerable<DeviceItemViewModel> query = _allDevices;

        if (!string.IsNullOrWhiteSpace(DeviceSearchQuery))
        {
            var searchTerm = DeviceSearchQuery.Trim();
            query = query.Where(item =>
                IsDeviceSearchNameEnabled && item.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                || IsDeviceSearchTypeEnabled && item.DeviceTypeText.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
        }

        query = ApplyDeviceSort(query, SelectedDeviceSortOption?.Key);

        var filtered = query.ToList();
        Devices.Clear();

        foreach (var device in filtered)
            Devices.Add(device);

        RaiseDeviceCollectionStateChanged();

        if (preserveSelection && preferredSelectionId.HasValue)
            SelectedDevice = _allDevices.FirstOrDefault(item => item.DeviceId == preferredSelectionId.Value);
    }

    private static IEnumerable<DeviceItemViewModel> ApplyDeviceSort(
        IEnumerable<DeviceItemViewModel> devices,
        string? sortKey)
    {
        return sortKey switch
        {
            "name-desc" => devices
                .OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "status-online-first" => devices
                .OrderBy(GetOnlineFirstStatusRank)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "status-issues-first" => devices
                .OrderBy(GetIssuesFirstStatusRank)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "linked-desc" => devices
                .OrderByDescending(item => item.LinkedAt)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "linked-asc" => devices
                .OrderBy(item => item.LinkedAt)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "last-sync-desc" => devices
                .OrderBy(item => item.LastSync is null)
                .ThenByDescending(item => item.LastSync)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "last-sync-asc" => devices
                .OrderByDescending(item => item.LastSync is null)
                .ThenBy(item => item.LastSync)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => devices
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private static int GetOnlineFirstStatusRank(DeviceItemViewModel device)
    {
        if (device.IsBlocked)
            return 3;

        if (device.ShowNotTrustedStatus)
            return 2;

        return device.IsOnline ? 0 : 1;
    }

    private static int GetIssuesFirstStatusRank(DeviceItemViewModel device)
    {
        if (device.IsBlocked)
            return 0;

        if (device.ShowNotTrustedStatus)
            return 1;

        return device.IsOnline ? 3 : 2;
    }

    private void RebuildDeviceSortOptions()
    {
        DeviceSortOptions.Clear();
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("name-asc", GetTranslation("Passwords_Sort_NameAsc")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("name-desc", GetTranslation("Passwords_Sort_NameDesc")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("status-online-first", GetTranslation("Profile_Device_Sort_StatusOnlineFirst")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("status-issues-first", GetTranslation("Profile_Device_Sort_StatusIssuesFirst")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("linked-desc", GetTranslation("Profile_Device_Sort_LinkedNewest")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("linked-asc", GetTranslation("Profile_Device_Sort_LinkedOldest")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("last-sync-desc", GetTranslation("Profile_Device_Sort_LastSyncNewest")));
        DeviceSortOptions.Add(new PasswordSortOptionViewModel("last-sync-asc", GetTranslation("Profile_Device_Sort_LastSyncOldest")));
    }

    private void SelectDefaultDeviceSortOption() =>
        SelectedDeviceSortOption = DeviceSortOptions.FirstOrDefault(item => item.Key == "name-asc") ?? DeviceSortOptions.FirstOrDefault();

    private void SetDeviceSearchMode(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        if (!value && EnabledDeviceSearchModeCount <= 1)
        {
            this.RaisePropertyChanged(propertyName);
            RaiseDeviceSearchModeToggleProperties();
            return;
        }

        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        RaiseDeviceSearchModeToggleProperties();
        ApplyDeviceFiltersAndSorting(SelectedDevice?.DeviceId, preserveSelection: true);
    }

    private bool CanToggleDeviceSearchMode(bool isEnabled) => !isEnabled || EnabledDeviceSearchModeCount > 1;

    private void RaiseDeviceSearchModeToggleProperties()
    {
        this.RaisePropertyChanged(nameof(CanToggleDeviceSearchName));
        this.RaisePropertyChanged(nameof(CanToggleDeviceSearchType));
    }

    private int EnabledDeviceSearchModeCount =>
        (_isDeviceSearchNameEnabled ? 1 : 0)
        + (_isDeviceSearchTypeEnabled ? 1 : 0);

    private void UpdateDeviceSortOptionSelectionMarks()
    {
        foreach (var option in DeviceSortOptions)
            option.IsSelected = ReferenceEquals(option, SelectedDeviceSortOption);
    }

    private void SelectDeviceSortOptionByKey(string key)
    {
        var option = DeviceSortOptions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

        if (option is not null)
            SelectedDeviceSortOption = option;
    }

    private string BuildDeviceSortMenuLabel(string key, string translationKey) =>
        $"{(string.Equals(SelectedDeviceSortOption?.Key, key, StringComparison.Ordinal) ? "✓ " : "   ")}{GetTranslation(translationKey)}";

    private void RaiseDeviceSortMenuLabelProperties()
    {
        this.RaisePropertyChanged(nameof(DeviceSortNameAscMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortNameDescMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortStatusOnlineFirstMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortStatusIssuesFirstMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortLinkedNewestMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortLinkedOldestMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortLastSyncNewestMenuLabel));
        this.RaisePropertyChanged(nameof(DeviceSortLastSyncOldestMenuLabel));
    }

    private void ApplyLocalizationToDeviceItems()
    {
        var localization = CreateDeviceItemLocalization();
        foreach (var device in _allDevices)
            device.ApplyLocalization(localization);
    }

    private void RaiseDeviceCollectionStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasDevices));
        this.RaisePropertyChanged(nameof(IsDevicesEmpty));
        this.RaisePropertyChanged(nameof(HasKnownDevices));
        this.RaisePropertyChanged(nameof(IsDeviceListEmpty));
        this.RaisePropertyChanged(nameof(IsDeviceSearchResultEmpty));
    }
    protected override void DisposeManaged()
    {
        Reset();
        base.DisposeManaged();
    }

}
