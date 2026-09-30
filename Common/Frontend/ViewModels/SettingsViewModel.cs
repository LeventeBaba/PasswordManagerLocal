using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly DeviceAppPreferencesService _deviceAppPreferences;
    private bool _isAppearanceSectionExpanded;
    private bool _isBackgroundSectionExpanded;
    private bool _isBackgroundSyncEnabled;
    private bool _isBackgroundSyncAvailable;
    private bool _isLoadingBackgroundSync;
    private bool _isChangingBackgroundSync;
    private bool _isBackgroundSyncDegraded;
    private bool _isExternalBackgroundSyncTransition;
    private bool _isApplyingBackgroundState;

    public SettingsViewModel(
        UiPreferencesService uiPreferences,
        DeviceAppPreferencesService deviceAppPreferences,
        Action navigateBack)
        : base(uiPreferences)
    {
        _deviceAppPreferences = deviceAppPreferences
            ?? throw new ArgumentNullException(nameof(deviceAppPreferences));
        ArgumentNullException.ThrowIfNull(navigateBack);

        NavigateBackCommand = Own(ReactiveCommand.Create(navigateBack));
        ToggleAppearanceSectionCommand = Own(ReactiveCommand.Create(ToggleAppearanceSection));
        ToggleBackgroundSectionCommand = Own(ReactiveCommand.Create(ToggleBackgroundSection));
        LanguageOptions =
        [
            new SettingsLanguageOptionViewModel(AppLanguage.English, GetTranslation("Language_English")),
            new SettingsLanguageOptionViewModel(AppLanguage.Hungarian, GetTranslation("Language_Hungarian"))
        ];
        ThemeOptions =
        [
            new SettingsThemeOptionViewModel(AppThemeMode.Light, GetTranslation("Theme_Light")),
            new SettingsThemeOptionViewModel(AppThemeMode.Dark, GetTranslation("Theme_Dark"))
        ];
        _deviceAppPreferences.PreferencesChanged += HandleDeviceAppPreferencesChanged;
        ApplyBackgroundState(_deviceAppPreferences.BackgroundSyncState);
    }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleAppearanceSectionCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ToggleBackgroundSectionCommand { get; }

    public bool IsAppearanceSectionExpanded
    {
        get => _isAppearanceSectionExpanded;
        private set
        {
            if (_isAppearanceSectionExpanded == value)
                return;

            this.RaiseAndSetIfChanged(ref _isAppearanceSectionExpanded, value);
            this.RaisePropertyChanged(nameof(IsAppearanceSectionCollapsed));
        }
    }

    public bool IsAppearanceSectionCollapsed => !IsAppearanceSectionExpanded;

    public bool IsBackgroundSectionExpanded
    {
        get => _isBackgroundSectionExpanded;
        private set
        {
            if (_isBackgroundSectionExpanded == value)
                return;

            this.RaiseAndSetIfChanged(ref _isBackgroundSectionExpanded, value);
            this.RaisePropertyChanged(nameof(IsBackgroundSectionCollapsed));
        }
    }

    public bool IsBackgroundSectionCollapsed => !IsBackgroundSectionExpanded;

    public IReadOnlyList<SettingsLanguageOptionViewModel> LanguageOptions { get; }

    public SettingsLanguageOptionViewModel? SelectedLanguageOption
    {
        get => LanguageOptions.FirstOrDefault(option => option.Language == CurrentLanguage);
        set
        {
            if (value is not null && value.Language != CurrentLanguage)
                UiPreferences.CurrentLanguage = value.Language;
        }
    }

    public IReadOnlyList<SettingsThemeOptionViewModel> ThemeOptions { get; }

    public SettingsThemeOptionViewModel? SelectedThemeOption
    {
        get => ThemeOptions.FirstOrDefault(option => option.Theme == CurrentThemeMode);
        set
        {
            if (value is not null && value.Theme != CurrentThemeMode)
                UiPreferences.CurrentThemeMode = value.Theme;
        }
    }

    public bool IsInterfaceAnimationsControlVisible => OperatingSystem.IsAndroid();

    public bool IsInterfaceAnimationsEnabled
    {
        get => InterfaceAnimationsEnabled;
        set
        {
            if (value != InterfaceAnimationsEnabled)
                UiPreferences.InterfaceAnimationsEnabled = value;
        }
    }

    public bool IsBackgroundSyncEnabled
    {
        get => _isBackgroundSyncEnabled;
        set
        {
            if (_isApplyingBackgroundState ||
                !IsBackgroundSyncToggleEnabled ||
                _isBackgroundSyncEnabled == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isBackgroundSyncEnabled, value);
            _ = ChangeBackgroundSyncAsync(value);
        }
    }

    public bool IsBackgroundSyncControlVisible => _isBackgroundSyncAvailable;

    public bool IsBackgroundSyncToggleEnabled =>
        _isBackgroundSyncAvailable &&
        !_isLoadingBackgroundSync &&
        !_isChangingBackgroundSync &&
        !_isExternalBackgroundSyncTransition;

    public bool IsBackgroundSyncProgressVisible =>
        _isLoadingBackgroundSync || _isChangingBackgroundSync ||
        _isExternalBackgroundSyncTransition;

    public string BackgroundSyncStatus
    {
        get
        {
            if (_isLoadingBackgroundSync)
                return GetTranslation("Settings_BackgroundSync_Loading");
            if (_isChangingBackgroundSync)
            {
                return GetTranslation(_isBackgroundSyncEnabled
                    ? "Settings_BackgroundSync_Enabling"
                    : "Settings_BackgroundSync_Disabling");
            }
            if (_isExternalBackgroundSyncTransition)
                return GetTranslation("Settings_BackgroundSync_Updating");
            if (!_isBackgroundSyncAvailable)
                return GetTranslation("Settings_BackgroundSync_Unavailable");
            if (_isBackgroundSyncDegraded)
                return GetTranslation("Settings_BackgroundSync_Degraded");
            return GetTranslation(_isBackgroundSyncEnabled
                ? "Settings_BackgroundSync_Enabled"
                : "Settings_BackgroundSync_Disabled");
        }
    }

    public string Title => GetTranslation("Settings");
    public string Subtitle => GetTranslation("Settings_Subtitle");
    public string BackLabel => GetTranslation("Common_Back");
    public string AppearanceTitle => GetTranslation("Settings_Appearance_Title");
    public string AppearanceDescription => GetTranslation("Settings_Appearance_Description");
    public string LanguageLabel => GetTranslation("Settings_Language");
    public string LanguageDescription => GetTranslation("Settings_Language_Description");
    public string ThemeLabel => GetTranslation("Settings_Theme");
    public string ThemeDescription => GetTranslation("Settings_Theme_Description");
    public string InterfaceAnimationsLabel => GetTranslation("Settings_InterfaceAnimations_Label");
    public string InterfaceAnimationsDescription => GetTranslation("Settings_InterfaceAnimations_Description");
    public string BackgroundOperationTitle => GetTranslation("Settings_Background_Title");
    public string BackgroundOperationDescription => GetTranslation("Settings_Background_Description");
    public string BackgroundSyncLabel => GetTranslation("Settings_BackgroundSync_Label");
    public string BackgroundSyncDescription => GetTranslation("Settings_BackgroundSync_Description");
    public string OnLabel => GetTranslation("Common_On");
    public string OffLabel => GetTranslation("Common_Off");

    public async Task LoadBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isLoadingBackgroundSync || _isChangingBackgroundSync)
            return;

        _isLoadingBackgroundSync = true;
        RaiseBackgroundOperationProperties();
        try
        {
            var state = await _deviceAppPreferences.RefreshBackgroundSyncAsync(cancellationToken);
            ApplyBackgroundState(state);
            if (!state.IsAvailable || state.IsDegraded)
                ShowErrorMessage(GetBackgroundSyncFailureMessage(state));
        }
        finally
        {
            _isLoadingBackgroundSync = false;
            RaiseBackgroundOperationProperties();
        }
    }

    protected override void OnLanguageChanged()
    {
        LanguageOptions[0].UpdateDisplayName(GetTranslation("Language_English"));
        LanguageOptions[1].UpdateDisplayName(GetTranslation("Language_Hungarian"));
        ThemeOptions[0].UpdateDisplayName(GetTranslation("Theme_Light"));
        ThemeOptions[1].UpdateDisplayName(GetTranslation("Theme_Dark"));

        RaisePropertiesChanged(
        [
            nameof(Title),
            nameof(Subtitle),
            nameof(BackLabel),
            nameof(AppearanceTitle),
            nameof(AppearanceDescription),
            nameof(LanguageLabel),
            nameof(LanguageDescription),
            nameof(ThemeLabel),
            nameof(ThemeDescription),
            nameof(InterfaceAnimationsLabel),
            nameof(InterfaceAnimationsDescription),
            nameof(SelectedLanguageOption),
            nameof(SelectedThemeOption),
            nameof(BackgroundOperationTitle),
            nameof(BackgroundOperationDescription),
            nameof(BackgroundSyncLabel),
            nameof(BackgroundSyncDescription),
            nameof(BackgroundSyncStatus),
            nameof(OnLabel),
            nameof(OffLabel)
        ]);
    }

    protected override void OnThemeChanged() =>
        this.RaisePropertyChanged(nameof(SelectedThemeOption));

    protected override void OnInterfaceMotionChanged() =>
        this.RaisePropertyChanged(nameof(IsInterfaceAnimationsEnabled));

    private async Task ChangeBackgroundSyncAsync(bool isEnabled)
    {
        if (_isChangingBackgroundSync)
            return;

        _isChangingBackgroundSync = true;
        RaiseBackgroundOperationProperties();
        try
        {
            var result = await _deviceAppPreferences.SetBackgroundSyncEnabledAsync(isEnabled);
            ApplyBackgroundState(result.State);
            if (!result.State.IsAvailable || result.State.IsDegraded)
            {
                ShowErrorMessage(GetBackgroundSyncFailureMessage(result.State));
            }
            else if (result.WasOutcomeUncertain)
            {
                ShowInformationMessage(GetTranslation("Settings_BackgroundSync_ReadBackGuidance"));
            }
            else
            {
                ShowSuccessMessage(GetTranslation(result.State.IsEnabled
                    ? "Settings_BackgroundSync_Enabled"
                    : "Settings_BackgroundSync_Disabled"));
            }
        }
        finally
        {
            _isChangingBackgroundSync = false;
            RaiseBackgroundOperationProperties();
        }
    }


    private string GetBackgroundSyncFailureMessage(BackgroundSyncClientState state) =>
        GetTranslation(state.FailureKind switch
        {
            BackgroundSyncClientFailureKind.StartupRegistration =>
                "Settings_BackgroundSync_StartupFailure",
            BackgroundSyncClientFailureKind.SettingPersistence =>
                "Settings_BackgroundSync_PersistenceFailure",
            BackgroundSyncClientFailureKind.Unavailable =>
                "Settings_BackgroundSync_Unavailable",
            _ => "Settings_BackgroundSync_Degraded"
        });

    private void ApplyBackgroundState(BackgroundSyncClientState state)
    {
        _isApplyingBackgroundState = true;
        try
        {
            this.RaiseAndSetIfChanged(ref _isBackgroundSyncEnabled, state.IsEnabled,
                nameof(IsBackgroundSyncEnabled));
            _isBackgroundSyncAvailable = state.IsAvailable;
            _isBackgroundSyncDegraded = state.IsDegraded;
            _isExternalBackgroundSyncTransition = state.IsTransitionInProgress;
        }
        finally
        {
            _isApplyingBackgroundState = false;
        }

        RaiseBackgroundOperationProperties();
    }

    private void RaiseBackgroundOperationProperties()
    {
        RaisePropertiesChanged(
        [
            nameof(IsBackgroundSyncEnabled),
            nameof(IsBackgroundSyncControlVisible),
            nameof(IsBackgroundSyncToggleEnabled),
            nameof(IsBackgroundSyncProgressVisible),
            nameof(BackgroundSyncStatus)
        ]);
    }

    private void ToggleAppearanceSection()
    {
        var shouldExpand = !IsAppearanceSectionExpanded;
        IsAppearanceSectionExpanded = shouldExpand;
        if (shouldExpand)
            IsBackgroundSectionExpanded = false;
    }

    private void ToggleBackgroundSection()
    {
        var shouldExpand = !IsBackgroundSectionExpanded;
        IsBackgroundSectionExpanded = shouldExpand;
        if (shouldExpand)
            IsAppearanceSectionExpanded = false;
    }

    private void HandleDeviceAppPreferencesChanged(
        object? sender,
        DeviceAppPreferencesChangedEventArgs args) =>
        ApplyBackgroundState(args.State);
    protected override void DisposeManaged()
    {
        _deviceAppPreferences.PreferencesChanged -= HandleDeviceAppPreferencesChanged;
        base.DisposeManaged();
    }

}
