using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.Helpers;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using ReactiveUI;
using ReactiveUI.Primitives;

using PasswordManagerLocal.Common.Contracts.Enrollment;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Auth;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly IEndpoints _endpoints;
    private readonly Action _navigateToRegistration;
    private readonly Func<Guid, Task> _onAuthenticationSucceededAsync;
    private readonly IFrontendDeadlineScheduler _deadlineScheduler;
    private Func<Task>? _navigateBackAsync;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _rememberMe;
    private bool _isPasswordVisible;
    private bool _isBusy;
    private bool _isBackendInitialized;
    private bool _isBackButtonVisible;
    private bool _isDeviceTransferIntroVisible;
    private bool _isDeviceTransferCodeVisible;
    private bool _isDeviceTransferFinished;
    private bool _isDeviceTransferSuccess;
    private string _deviceTransferCode = string.Empty;
    private Bitmap? _deviceTransferQrCode;
    private bool _isDeviceTransferCodeCopied;
    private DateTimeOffset? _enrollmentExpirationQueriedAt;
    private IDisposable? _deviceTransferExpirationTimer;
    private IDisposable? _deviceTransferCopyFeedbackTimer;

    public LoginViewModel(
        UiPreferencesService uiPreferences,
        IEndpoints endpoints,
        Action navigateToRegistration,
        Func<Guid, Task> onAuthenticationSucceededAsync,
        IFrontendDeadlineScheduler? deadlineScheduler = null)
        : base(uiPreferences)
    {
        _endpoints = endpoints;
        _navigateToRegistration = navigateToRegistration;
        _onAuthenticationSucceededAsync = onAuthenticationSucceededAsync;
        _deadlineScheduler = deadlineScheduler ?? new AvaloniaFrontendDeadlineScheduler();

        LoginCommand = Own(ReactiveCommand.CreateFromTask(LoginAsync));
        ExecutePrimaryActionCommand = Own(ReactiveCommand.CreateFromTask(ExecutePrimaryActionAsync));
        NavigateToRegistrationCommand = Own(ReactiveCommand.Create(_navigateToRegistration));
        NavigateBackCommand = Own(ReactiveCommand.CreateFromTask(NavigateBackAsync));
        TogglePasswordVisibilityCommand = Own(ReactiveCommand.Create(TogglePasswordVisibility));
        ShowDeviceTransferIntroCommand = Own(ReactiveCommand.Create(ShowDeviceTransferIntro));
        StartDeviceTransferCommand = Own(ReactiveCommand.CreateFromTask(StartDeviceTransferAsync));
        CancelDeviceTransferCommand = Own(ReactiveCommand.CreateFromTask(CancelDeviceTransferAsync));
        FinishDeviceTransferCommand = Own(ReactiveCommand.Create(FinishDeviceTransfer));
        CopyDeviceTransferCodeCommand = Own(ReactiveCommand.CreateFromTask(CopyDeviceTransferCodeAsync));
    }

    public string Username
    {
        get => _username;
        set
        {
            this.RaiseAndSetIfChanged(ref _username, value);
            this.RaisePropertyChanged(nameof(CanLogin));
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            this.RaiseAndSetIfChanged(ref _password, value);
            this.RaisePropertyChanged(nameof(CanLogin));
        }
    }

    public bool RememberMe
    {
        get => _rememberMe;
        set => this.RaiseAndSetIfChanged(ref _rememberMe, value);
    }

    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isPasswordVisible, value);
            this.RaisePropertyChanged(nameof(PasswordMaskCharacter));
            this.RaisePropertyChanged(nameof(PasswordVisibilityToggleText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanLogin));
            this.RaisePropertyChanged(nameof(CanStartDeviceTransfer));
            this.RaisePropertyChanged(nameof(CanAutomaticallyRefreshEnrollment));
        }
    }

    public bool IsBackendInitialized
    {
        get => _isBackendInitialized;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBackendInitialized, value);
            this.RaisePropertyChanged(nameof(CanLogin));
            this.RaisePropertyChanged(nameof(CanStartDeviceTransfer));
        }
    }

    public bool IsBackButtonVisible
    {
        get => _isBackButtonVisible;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBackButtonVisible, value);
            this.RaisePropertyChanged(nameof(IsHeaderBackButtonVisible));
        }
    }

    public bool IsLoginFormVisible => !IsDeviceTransferIntroVisible && !IsDeviceTransferCodeVisible && !IsDeviceTransferFinished;

    public bool IsDeviceTransferFlowVisible =>
        IsDeviceTransferIntroVisible || IsDeviceTransferCodeVisible || IsDeviceTransferFinished;

    public bool IsDeviceTransferBackButtonVisible =>
        IsDeviceTransferIntroVisible || IsDeviceTransferCodeVisible;

    public bool IsHeaderBackButtonVisible =>
        IsBackButtonVisible || IsDeviceTransferBackButtonVisible;

    public bool IsDeviceTransferIntroVisible
    {
        get => _isDeviceTransferIntroVisible;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDeviceTransferIntroVisible, value);
            this.RaisePropertyChanged(nameof(CanStartDeviceTransfer));
            RaiseDeviceTransferViewStateChanged();
        }
    }

    public bool IsDeviceTransferCodeVisible
    {
        get => _isDeviceTransferCodeVisible;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDeviceTransferCodeVisible, value);
            RaiseDeviceTransferViewStateChanged();
        }
    }

    public bool IsDeviceTransferFinished
    {
        get => _isDeviceTransferFinished;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDeviceTransferFinished, value);
            RaiseDeviceTransferViewStateChanged();
        }
    }

    public bool IsDeviceTransferSuccess
    {
        get => _isDeviceTransferSuccess;
        private set => this.RaiseAndSetIfChanged(ref _isDeviceTransferSuccess, value);
    }

    public string DeviceTransferCode
    {
        get => _deviceTransferCode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _deviceTransferCode, value);
            this.RaisePropertyChanged(nameof(ReadableDeviceTransferCode));
            DeviceTransferQrCode = EnrollmentQrCodeService.CreateQrCodeBitmap(value);
        }
    }

    public string ReadableDeviceTransferCode => BuildReadableCode(DeviceTransferCode);

    public Bitmap? DeviceTransferQrCode
    {
        get => _deviceTransferQrCode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _deviceTransferQrCode, value);
            this.RaisePropertyChanged(nameof(HasDeviceTransferQrCode));
        }
    }

    public bool HasDeviceTransferQrCode => DeviceTransferQrCode is not null;

    public bool IsDeviceTransferCodeCopied
    {
        get => _isDeviceTransferCodeCopied;
        private set
        {
            if (_isDeviceTransferCodeCopied == value)
                return;

            this.RaiseAndSetIfChanged(ref _isDeviceTransferCodeCopied, value);
            this.RaisePropertyChanged(nameof(IsDeviceTransferCodeNotCopied));
            this.RaisePropertyChanged(nameof(DeviceTransferCopyCodeLabel));
        }
    }

    public bool IsDeviceTransferCodeNotCopied => !IsDeviceTransferCodeCopied;

    public bool CanLogin =>
        IsBackendInitialized &&
        !IsBusy &&
        IsLoginFormVisible &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);

    public bool CanStartDeviceTransfer =>
        IsBackendInitialized &&
        !IsBusy &&
        IsDeviceTransferIntroVisible;

    public OperationMessageState DeviceTransferStatus { get; } = new();

    public char PasswordMaskCharacter => IsPasswordVisible ? '\0' : '●';

    public ReactiveCommand<RxVoid, RxVoid> LoginCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ExecutePrimaryActionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> NavigateToRegistrationCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ShowDeviceTransferIntroCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StartDeviceTransferCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelDeviceTransferCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> FinishDeviceTransferCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CopyDeviceTransferCodeCommand { get; }

    public string Title => IsDeviceTransferFlowVisible
        ? GetTranslation("Login_DeviceTransfer_Title")
        : GetTranslation("Login_Title");

    public string BackLabel => GetTranslation("Common_Back");

    public string Subtitle => IsDeviceTransferFlowVisible
        ? GetTranslation("Login_DeviceTransfer_Subtitle")
        : GetTranslation("Login_Subtitle");

    public string UsernameLabel => GetTranslation("Login_Username_Label");

    public string PasswordLabel => GetTranslation("Login_Password_Label");

    public string RememberMeLabel => GetTranslation("Login_RememberMe_Label");

    public string RememberMeOnLabel => GetTranslation("Common_On");

    public string RememberMeOffLabel => GetTranslation("Common_Off");

    public string LoginButtonLabel => GetTranslation("Login_Button");

    public string NavigateToRegisterLabel => GetTranslation("Login_NavigateToRegister_Button");

    public string NoAccountText => GetTranslation("Login_NoAccount_Text");

    public string UsernamePlaceholder => GetTranslation("Login_Username_Placeholder");

    public string PasswordPlaceholder => GetTranslation("Login_Password_Placeholder");

    public string PasswordVisibilityToggleText => GetTranslation(IsPasswordVisible ? "Common_Hide" : "Common_Show");

    public string DeviceTransferButtonLabel => GetTranslation("Login_DeviceTransfer_Button");

    public string DeviceTransferButtonDescription => GetTranslation("Login_DeviceTransfer_Subtitle");

    public string DeviceTransferTitle => GetTranslation("Login_DeviceTransfer_Title");

    public string DeviceTransferDescription => GetTranslation("Login_DeviceTransfer_Description");

    public string DeviceTransferStartLabel => GetTranslation("Login_DeviceTransfer_Start");

    public string DeviceTransferCodeDescription => GetTranslation("Login_DeviceTransfer_CodeDescription");

    public string DeviceTransferCodeReadabilityHint => GetTranslation("Login_DeviceTransfer_CodeReadabilityHint");

    public string DeviceTransferCopyCodeLabel => GetTranslation(IsDeviceTransferCodeCopied
        ? "Login_DeviceTransfer_CopyCode_Copied"
        : "Login_DeviceTransfer_CopyCode");

    public string DeviceTransferFinishTitle => IsDeviceTransferSuccess
        ? GetTranslation("Login_DeviceTransfer_SuccessTitle")
        : GetTranslation("Login_DeviceTransfer_ErrorTitle");

    public string DeviceTransferFinishLabel => GetTranslation("Common_Ok");

    public string CancelLabel => GetTranslation("Common_Cancel");

    public string BusyText => GetTranslation("Common_Loading");

    protected override void OnLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(BackLabel));
        this.RaisePropertyChanged(nameof(Subtitle));
        this.RaisePropertyChanged(nameof(UsernameLabel));
        this.RaisePropertyChanged(nameof(PasswordLabel));
        this.RaisePropertyChanged(nameof(RememberMeLabel));
        this.RaisePropertyChanged(nameof(RememberMeOnLabel));
        this.RaisePropertyChanged(nameof(RememberMeOffLabel));
        this.RaisePropertyChanged(nameof(LoginButtonLabel));
        this.RaisePropertyChanged(nameof(NavigateToRegisterLabel));
        this.RaisePropertyChanged(nameof(NoAccountText));
        this.RaisePropertyChanged(nameof(UsernamePlaceholder));
        this.RaisePropertyChanged(nameof(PasswordPlaceholder));
        this.RaisePropertyChanged(nameof(PasswordVisibilityToggleText));
        this.RaisePropertyChanged(nameof(DeviceTransferButtonLabel));
        this.RaisePropertyChanged(nameof(DeviceTransferButtonDescription));
        this.RaisePropertyChanged(nameof(DeviceTransferTitle));
        this.RaisePropertyChanged(nameof(DeviceTransferDescription));
        this.RaisePropertyChanged(nameof(DeviceTransferStartLabel));
        this.RaisePropertyChanged(nameof(DeviceTransferCodeDescription));
        this.RaisePropertyChanged(nameof(DeviceTransferCodeReadabilityHint));
        this.RaisePropertyChanged(nameof(DeviceTransferCopyCodeLabel));
        this.RaisePropertyChanged(nameof(DeviceTransferFinishTitle));
        this.RaisePropertyChanged(nameof(DeviceTransferFinishLabel));
        this.RaisePropertyChanged(nameof(CancelLabel));
        this.RaisePropertyChanged(nameof(BusyText));
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        AbandonActiveDeviceTransfer();
        ResetDeviceTransferState(true);
    }

    public void Reset()
    {
        AbandonActiveDeviceTransfer();
        Username = string.Empty;
        Password = string.Empty;
        RememberMe = false;
        IsPasswordVisible = false;
        ClearStatusMessage();
        ResetDeviceTransferState(false);
    }


    public async Task<bool> TryNavigateBackAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        if (IsDeviceTransferCodeVisible)
        {
            await CancelDeviceTransferAsync();
            return true;
        }

        if (IsDeviceTransferIntroVisible || IsDeviceTransferFinished)
        {
            ResetDeviceTransferState(true);
            return true;
        }

        return false;
    }



    internal void SetBackendInitialized(bool isInitialized) =>
        IsBackendInitialized = isInitialized;

    public void SetBackNavigation(bool isVisible, Func<Task>? navigateBackAsync)
    {
        IsBackButtonVisible = isVisible;
        _navigateBackAsync = navigateBackAsync;
    }


    private async Task NavigateBackAsync()
    {
        if (IsBusy)
            return;

        if (await TryNavigateBackAsync())
            return;

        if (_navigateBackAsync is not null)
            await _navigateBackAsync();
    }

    private async Task ExecutePrimaryActionAsync()
    {
        if (IsBusy)
            return;

        if (IsLoginFormVisible)
        {
            if (CanLogin)
                await LoginAsync();

            return;
        }

        if (IsDeviceTransferIntroVisible)
        {
            if (CanStartDeviceTransfer)
                await StartDeviceTransferAsync();

            return;
        }

        if (IsDeviceTransferCodeVisible)
            return;

        if (IsDeviceTransferFinished)
        {
            FinishDeviceTransfer();
        }
    }


    private async Task LoginAsync()
    {
        if (!CanLogin)
            return;

        ClearStatusMessage();

        if (string.IsNullOrWhiteSpace(Username))
        {
            ShowErrorMessage(GetTranslation("Validation_Username_Required"));
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ShowErrorMessage(GetTranslation("Validation_Password_Required"));
            return;
        }

        var passwordHash = SecretTransform.HashPassword(Password);
        Password = string.Empty;

        try
        {
            IsBusy = true;
            var token = await _endpoints.LoginAsync(new LoginRequest
            {
                Username = Username.Trim(),
                Password = passwordHash,
                RememberMe = RememberMe
            });

            await _onAuthenticationSucceededAsync(token);
            Reset();
        }
        catch (Exception ex)
        {
            ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        finally
        {
            IsBusy = false;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordHash);
        }
    }

    private void ShowDeviceTransferIntro()
    {
        ClearStatusMessage();
        IsDeviceTransferIntroVisible = true;
        IsDeviceTransferCodeVisible = false;
        IsDeviceTransferFinished = false;
        DeviceTransferStatus.Clear();
    }

    private async Task StartDeviceTransferAsync()
    {
        if (!CanStartDeviceTransfer)
            return;

        DeviceTransferStatus.Clear();
        DateTimeOffset? expirationToSchedule = null;
        _enrollmentExpirationQueriedAt = null;

        try
        {
            IsBusy = true;
            if (!await FirewallPermissionStartupPrompt.EnsureConfiguredAsync(CurrentLanguage))
            {
                DeviceTransferStatus.ShowError(GetTranslation("Firewall_RequiredForLocalNetwork"));
                return;
            }

            var response = await _endpoints.StartDeviceEnrollmentAsync();
            expirationToSchedule = response.ExpiresAt;
            DeviceTransferCode = response.Code;
            IsDeviceTransferIntroVisible = false;
            IsDeviceTransferCodeVisible = true;
            IsDeviceTransferFinished = false;
            var initialStatus = response.InitialStatus ?? new DeviceEnrollmentStatusResponse
            {
                State = DeviceEnrollmentState.Waiting,
                ExpiresAt = response.ExpiresAt
            };
            if (!initialStatus.ExpiresAt.HasValue)
                initialStatus.ExpiresAt = response.ExpiresAt;
            ApplyDeviceTransferStatus(initialStatus);
        }
        catch (Exception ex)
        {
            await FirewallPermissionStartupPrompt.RevalidateAfterLikelyFirewallFailureAsync(
                ex,
                CurrentLanguage);
            DeviceTransferStatus.ShowError(GetSafeErrorMessage(ex));
            IsDeviceTransferSuccess = false;
            IsDeviceTransferIntroVisible = false;
            IsDeviceTransferCodeVisible = false;
            IsDeviceTransferFinished = true;
            this.RaisePropertyChanged(nameof(DeviceTransferFinishTitle));
        }
        finally
        {
            IsBusy = false;
            if (IsDeviceTransferCodeVisible && expirationToSchedule is { } expiresAt)
                ScheduleEnrollmentExpiration(expiresAt);
        }
    }



    private async Task CopyDeviceTransferCodeAsync()
    {
        try
        {
            if (await TryCopyTextToClipboardAsync(DeviceTransferCode))
            {
                ShowDeviceTransferCodeCopiedFeedback();
                return;
            }

            ResetDeviceTransferCodeCopiedFeedback();
            DeviceTransferStatus.ShowError(GetTranslation("Error_ClipboardUnavailable"));
        }
        catch
        {
            ResetDeviceTransferCodeCopiedFeedback();
            DeviceTransferStatus.ShowError(GetTranslation("Error_ClipboardUnavailable"));
        }
    }

    private void ShowDeviceTransferCodeCopiedFeedback()
    {
        if (IsDisposed)
            return;
        _deviceTransferCopyFeedbackTimer?.Dispose();
        IsDeviceTransferCodeCopied = true;
        _deviceTransferCopyFeedbackTimer = DispatcherTimer.RunOnce(() =>
        {
            _deviceTransferCopyFeedbackTimer = null;
            IsDeviceTransferCodeCopied = false;
        }, TimeSpan.FromSeconds(5));
    }

    private void ResetDeviceTransferCodeCopiedFeedback()
    {
        _deviceTransferCopyFeedbackTimer?.Dispose();
        _deviceTransferCopyFeedbackTimer = null;
        IsDeviceTransferCodeCopied = false;
    }

    private async Task CancelDeviceTransferAsync()
    {
        var shouldCancel = IsDeviceTransferCodeVisible;
        ResetDeviceTransferState(true);

        if (shouldCancel)
        {
            try { await _endpoints.CancelDeviceEnrollmentAsync(); }
            catch { }
        }
    }

    private void FinishDeviceTransfer() => ResetDeviceTransferState(true);

    private void AbandonActiveDeviceTransfer()
    {
        if (!IsDeviceTransferCodeVisible)
            return;
        ResetDeviceTransferState(true);
        _ = CancelEnrollmentBestEffortAsync();
    }

    private async Task CancelEnrollmentBestEffortAsync()
    {
        try { await _endpoints.CancelDeviceEnrollmentAsync(); }
        catch { }
    }

    private void ScheduleEnrollmentExpiration(DateTimeOffset expiresAt)
    {
        _deviceTransferExpirationTimer?.Dispose();
        _deviceTransferExpirationTimer = null;
        if (!CanAutomaticallyRefreshEnrollment)
            return;
        if (expiresAt <= DateTimeOffset.UtcNow && _enrollmentExpirationQueriedAt == expiresAt)
            return;

        IDisposable? scheduled = null;
        scheduled = _deadlineScheduler.Schedule(expiresAt, () =>
        {
            if (!ReferenceEquals(_deviceTransferExpirationTimer, scheduled))
                return;

            _deviceTransferExpirationTimer = null;
            _enrollmentExpirationQueriedAt = expiresAt;
            _ = RefreshEnrollmentAtExpirationAsync();
        });
        _deviceTransferExpirationTimer = scheduled;
    }

    private async Task RefreshEnrollmentAtExpirationAsync()
    {
        if (!CanAutomaticallyRefreshEnrollment || IsDisposed)
            return;

        try
        {
            var status = await _endpoints.GetDeviceEnrollmentStatusAsync(LifetimeToken);
            LifetimeToken.ThrowIfCancellationRequested();
            ApplyDeviceTransferStatus(status);
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DeviceTransferStatus.ShowError(GetSafeErrorMessage(ex));
        }
    }

    public bool CanAutomaticallyRefreshEnrollment =>
        IsDeviceTransferCodeVisible && !IsBusy && !IsDeviceTransferFinished;

    public async Task<bool> RefreshEnrollmentAutomaticallyAsync(
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        if (!CanAutomaticallyRefreshEnrollment)
            return false;

        var status = await _endpoints.GetDeviceEnrollmentStatusAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanAutomaticallyRefreshEnrollment || !isCurrent())
            return false;

        ApplyDeviceTransferStatus(status);
        return true;
    }

    private void ApplyDeviceTransferStatus(DeviceEnrollmentStatusResponse status)
    {
        if (IsDisposed)
            return;
        if (status.State == DeviceEnrollmentState.Waiting)
        {
            if (status.ExpiresAt is { } expiresAt)
            {
                ScheduleEnrollmentExpiration(expiresAt);
            }
            return;
        }

        if (status.State == DeviceEnrollmentState.Completed)
        {
            StopEnrollmentExpirationTimer();
            IsDeviceTransferSuccess = true;
            DeviceTransferStatus.ShowSuccess(GetTranslation("Login_DeviceTransfer_SuccessMessage"));
            IsDeviceTransferCodeVisible = false;
            IsDeviceTransferFinished = true;
            this.RaisePropertyChanged(nameof(DeviceTransferFinishTitle));
            return;
        }

        if (status.State is DeviceEnrollmentState.Failed or DeviceEnrollmentState.Expired or
            DeviceEnrollmentState.Cancelled or DeviceEnrollmentState.None)
        {
            StopEnrollmentExpirationTimer();
            IsDeviceTransferSuccess = false;
            DeviceTransferStatus.ShowError(status.ErrorCode == PasswordManagerLocal.Common.Contracts.Enrollment.DeviceEnrollmentErrorCode.Unknown
                ? GetTranslation("Login_DeviceTransfer_ErrorMessage")
                : GetDeviceEnrollmentErrorMessage(status.ErrorCode));
            IsDeviceTransferCodeVisible = false;
            IsDeviceTransferFinished = true;
            this.RaisePropertyChanged(nameof(DeviceTransferFinishTitle));
        }
    }

    private void ResetDeviceTransferState(bool clearCode)
    {
        ResetDeviceTransferCodeCopiedFeedback();
        StopEnrollmentExpirationTimer();
        IsDeviceTransferIntroVisible = false;
        IsDeviceTransferCodeVisible = false;
        IsDeviceTransferFinished = false;
        IsDeviceTransferSuccess = false;
        DeviceTransferStatus.Clear();
        _enrollmentExpirationQueriedAt = null;

        if (clearCode)
            DeviceTransferCode = string.Empty;
    }

    private void StopEnrollmentExpirationTimer()
    {
        _deviceTransferExpirationTimer?.Dispose();
        _deviceTransferExpirationTimer = null;
    }

    private void RaiseDeviceTransferViewStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsLoginFormVisible));
        this.RaisePropertyChanged(nameof(IsDeviceTransferFlowVisible));
        this.RaisePropertyChanged(nameof(IsDeviceTransferBackButtonVisible));
        this.RaisePropertyChanged(nameof(IsHeaderBackButtonVisible));
        this.RaisePropertyChanged(nameof(CanAutomaticallyRefreshEnrollment));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Subtitle));
        this.RaisePropertyChanged(nameof(CanLogin));
    }

    private static string BuildReadableCode(string code) =>
        string.IsNullOrEmpty(code)
            ? string.Empty
            : code.Replace("0", "0\u0338", StringComparison.Ordinal);

    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;
    protected override void DisposeManaged()
    {
        Reset();
        DeviceTransferCode = string.Empty;
        DeviceTransferStatus.Dispose();
        base.DisposeManaged();
    }

}
