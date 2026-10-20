using PasswordManagerLocal.Common.Frontend.Exceptions;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Contracts.Errors;
using ReactiveUI;
using System.ComponentModel;

using PasswordManagerLocal.Common.Contracts.Enrollment;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public abstract class ViewModelBase : DisposableReactiveObject
{
    protected ViewModelBase(UiPreferencesService uiPreferences)
    {
        UiPreferences = uiPreferences;
        UiPreferences.PreferencesChanged += HandlePreferencesChanged;
        UiPreferences.MotionPolicy.Changed += HandleInterfaceMotionChanged;
        OperationMessage.PropertyChanged += HandleOperationMessageChanged;
    }

    protected UiPreferencesService UiPreferences { get; }
    public FrontendPlatformServices PlatformServices => UiPreferences.PlatformServices;
    protected CancellationToken LifetimeToken => PlatformServices.LifetimeToken;

    protected OperationMessageState OperationMessage { get; } = new();

    public AppLanguage CurrentLanguage => UiPreferences.CurrentLanguage;

    public AppThemeId CurrentThemeId => UiPreferences.CurrentThemeId;

    public ThemeBrightness CurrentThemeBrightness => UiPreferences.CurrentThemeBrightness;

    public bool InterfaceAnimationsEnabled => UiPreferences.InterfaceAnimationsEnabled;

    public bool IsAndroidUi => UiPreferences.MotionPolicy.IsAndroidUi;

    public bool EffectiveInterfaceAnimationsEnabled => UiPreferences.MotionPolicy.EffectiveInterfaceAnimationsEnabled;

    public string? StatusMessage => OperationMessage.Message;

    public string ScrollBarScrollHereLabel => GetTranslation("ScrollBar_Context_ScrollHere");

    public string ScrollBarTopLabel => GetTranslation("ScrollBar_Context_Top");

    public string ScrollBarBottomLabel => GetTranslation("ScrollBar_Context_Bottom");

    public string ScrollBarPageUpLabel => GetTranslation("ScrollBar_Context_PageUp");

    public string ScrollBarPageDownLabel => GetTranslation("ScrollBar_Context_PageDown");

    public string ScrollBarScrollUpLabel => GetTranslation("ScrollBar_Context_ScrollUp");

    public string ScrollBarScrollDownLabel => GetTranslation("ScrollBar_Context_ScrollDown");

    public string ScrollBarLeftEdgeLabel => GetTranslation("ScrollBar_Context_LeftEdge");

    public string ScrollBarRightEdgeLabel => GetTranslation("ScrollBar_Context_RightEdge");

    public string ScrollBarPageLeftLabel => GetTranslation("ScrollBar_Context_PageLeft");

    public string ScrollBarPageRightLabel => GetTranslation("ScrollBar_Context_PageRight");

    public string ScrollBarScrollLeftLabel => GetTranslation("ScrollBar_Context_ScrollLeft");

    public string ScrollBarScrollRightLabel => GetTranslation("ScrollBar_Context_ScrollRight");

    public bool HasStatusMessage => OperationMessage.HasMessage;

    public bool IsStatusMessageError => OperationMessage.IsError;

    public bool IsStatusMessageInformation => OperationMessage.IsInformation;

    public bool IsStatusMessageSuccess => OperationMessage.IsSuccess;

    public bool HasNonErrorStatusMessage => OperationMessage.HasNonErrorMessage;

    internal Func<string>? CaptureStatusMessageFactory() =>
        OperationMessage.CaptureMessageFactory();

    protected string GetTranslation(string key) => UiPreferences.GetString(key);

    protected void ShowInformationMessage(string message, bool autoDismiss = false) =>
        OperationMessage.ShowInformation(message, autoDismiss);

    protected void ShowInformationMessage(Func<string> messageFactory, bool autoDismiss = false) =>
        OperationMessage.ShowInformation(messageFactory, autoDismiss);

    protected void ShowLocalizedInformationMessage(string key, bool autoDismiss = false) =>
        OperationMessage.ShowInformation(() => GetTranslation(key), autoDismiss);

    protected void ShowSuccessMessage(string message) =>
        OperationMessage.ShowSuccess(message);

    protected void ShowSuccessMessage(Func<string> messageFactory) =>
        OperationMessage.ShowSuccess(messageFactory);

    protected void ShowLocalizedSuccessMessage(string key) =>
        OperationMessage.ShowSuccess(() => GetTranslation(key));

    protected void ShowErrorMessage(string message) =>
        OperationMessage.ShowError(message);

    protected void ShowErrorMessage(Func<string> messageFactory) =>
        OperationMessage.ShowError(messageFactory);

    protected void ShowLocalizedErrorMessage(string key) =>
        OperationMessage.ShowError(() => GetTranslation(key));

    protected void ShowSafeErrorMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var messageFactory = CreateSafeErrorMessageFactory(exception);
        OperationMessage.ShowError(messageFactory);
    }

    public void ClearStatusMessage() => OperationMessage.Clear();

    public virtual void OnNavigatedFrom() => ClearStatusMessage();

    protected string GetSafeErrorMessage(Exception exception) =>
        CreateSafeErrorMessageFactory(exception)();

    protected Func<string> CreateSafeErrorMessageFactory(Exception exception)
    {
        if (exception is EndpointFailureException endpointFailure)
            return CreateEndpointFailureMessageFactory(endpointFailure);

        var key = exception switch
        {
            InvalidTokenException => "Error_InvalidSession",
            UserNotFoundException => "Error_InvalidCredentials",
            UnauthorizedAccessException => "Error_InvalidCredentials",
            InvalidInputException => "Error_InvalidInput",
            PasswordNotFoundException => "Error_NotFound",
            DuplicatePasswordNameException => "Error_DuplicatePasswordName",
            DuplicatePasswordTagNameException => "Error_DuplicatePasswordTagName",
            PasswordTagNotFoundException => "Error_NotFound",
            DuplicateCustomUserColorNameException => "Error_DuplicateCustomColorName",
            DuplicateCustomUserColorCodeException => "Error_DuplicateCustomColorCode",
            CustomUserColorNotFoundException => "Error_NotFound",
            LimitReachedException => "Error_LimitReached",
            InvalidDataIntegrityException => "Error_DataIntegrity",
            DeviceIdentityNotInitilaizedException => "Error_DeviceIdentity",
            DuplicateActiveProfileException => "Error_ProfileAlreadyLoggedIn",
            OperationCanceledException => "Error_OperationCanceled",
            DeviceEnrollmentException => null,
            _ => "Error_Generic"
        };

        if (key is not null)
            return () => GetTranslation(key);

        var enrollmentException = (DeviceEnrollmentException)exception;
        var errorCode = enrollmentException.ErrorCode;
        var detail = enrollmentException.Message?.Trim();
        return () => GetDeviceEnrollmentErrorMessage(errorCode, detail);
    }

    private Func<string> CreateEndpointFailureMessageFactory(EndpointFailureException exception)
    {
        if (exception.FailureKind == EndpointFailureKind.DeviceEnrollment)
        {
            var errorCode = exception.EnrollmentErrorCode ?? DeviceEnrollmentErrorCode.Unknown;
            return () => GetDeviceEnrollmentErrorMessage(errorCode);
        }

        var key = exception.FailureKind switch
        {
            EndpointFailureKind.InvalidCredentials => "Error_InvalidCredentials",
            EndpointFailureKind.InvalidSession => "Error_InvalidSession",
            EndpointFailureKind.InvalidInput => "Error_InvalidInput",
            EndpointFailureKind.NotFound => "Error_NotFound",
            EndpointFailureKind.DuplicatePasswordName => "Error_DuplicatePasswordName",
            EndpointFailureKind.DuplicatePasswordTagName => "Error_DuplicatePasswordTagName",
            EndpointFailureKind.DuplicateCustomColorName => "Error_DuplicateCustomColorName",
            EndpointFailureKind.DuplicateCustomColorCode => "Error_DuplicateCustomColorCode",
            EndpointFailureKind.LimitReached => "Error_LimitReached",
            EndpointFailureKind.DataIntegrity => "Error_DataIntegrity",
            EndpointFailureKind.DeviceIdentityUnavailable => "Error_DeviceIdentity",
            EndpointFailureKind.RegistrationUsernameUnavailable => "Validation_Username_Unavailable",
            EndpointFailureKind.RegistrationUsernameAvailabilityIndeterminate =>
                "Validation_Username_AvailabilityIndeterminate",
            _ => "Error_Generic"
        };
        return () => GetTranslation(key);
    }

    protected string GetDeviceEnrollmentErrorMessage(DeviceEnrollmentException exception)
    {
        return GetDeviceEnrollmentErrorMessage(exception.ErrorCode, exception.Message?.Trim());
    }

    private string GetDeviceEnrollmentErrorMessage(
        DeviceEnrollmentErrorCode errorCode,
        string? detail)
    {
        var message = GetDeviceEnrollmentErrorMessage(errorCode);

        if (string.IsNullOrWhiteSpace(detail))
            return message;

        if (detail.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            detail.StartsWith("Hiba:", StringComparison.OrdinalIgnoreCase))
            return message;

        return $"{message}\n\n{GetTranslation("Error_TechnicalDetails")}: {detail}";
    }

    protected string GetDeviceEnrollmentErrorMessage(DeviceEnrollmentErrorCode errorCode) =>
        errorCode switch
        {
            DeviceEnrollmentErrorCode.SyncDisabled => GetTranslation("Error_DeviceEnrollment_SyncDisabled"),
            DeviceEnrollmentErrorCode.InvalidCode => GetTranslation("Error_DeviceEnrollment_InvalidCode"),
            DeviceEnrollmentErrorCode.NewDeviceNotFound => GetTranslation("Error_DeviceEnrollment_NewDeviceNotFound"),
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed => GetTranslation("Error_DeviceEnrollment_NewDeviceConnectionFailed"),
            DeviceEnrollmentErrorCode.NewDeviceRejected => GetTranslation("Error_DeviceEnrollment_NewDeviceRejected"),
            DeviceEnrollmentErrorCode.CodeExpired => GetTranslation("Error_DeviceEnrollment_CodeExpired"),
            DeviceEnrollmentErrorCode.CodeProofInvalid => GetTranslation("Error_DeviceEnrollment_CodeProofInvalid"),
            DeviceEnrollmentErrorCode.ProfileDataInvalid => GetTranslation("Error_DeviceEnrollment_ProfileDataInvalid"),
            DeviceEnrollmentErrorCode.ProfileDataTooLarge => GetTranslation("Error_DeviceEnrollment_ProfileDataTooLarge"),
            DeviceEnrollmentErrorCode.DeviceIdentityConflict => GetTranslation("Error_DeviceEnrollment_DeviceIdentityConflict"),
            DeviceEnrollmentErrorCode.LocalNetworkUnavailable => GetTranslation("Error_DeviceEnrollment_LocalNetworkUnavailable"),
            DeviceEnrollmentErrorCode.LocalEnrollmentListenerUnavailable => GetTranslation("Error_DeviceEnrollment_LocalEnrollmentListenerUnavailable"),
            _ => GetTranslation("Error_DeviceEnrollment_Generic")
        };

    protected Task<bool> TryCopyTextToClipboardAsync(string? text) =>
        PlatformServices.Clipboard.TrySetTextAsync(text);

    protected void RaisePropertiesChanged(IEnumerable<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
            this.RaisePropertyChanged(propertyName);
    }

    protected virtual void OnLanguageChanged()
    {
    }

    protected virtual void OnThemeChanged()
    {
    }

    protected virtual void OnInterfaceMotionChanged()
    {
    }

    protected virtual void OnStatusMessageChanged()
    {
    }

    protected override void DisposeManaged()
    {
        UiPreferences.PreferencesChanged -= HandlePreferencesChanged;
        UiPreferences.MotionPolicy.Changed -= HandleInterfaceMotionChanged;
        OperationMessage.PropertyChanged -= HandleOperationMessageChanged;
        OperationMessage.Dispose();
        base.DisposeManaged();
    }

    private void HandleOperationMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OperationMessageState.Message):
                this.RaisePropertyChanged(nameof(StatusMessage));
                break;
            case nameof(OperationMessageState.HasMessage):
                this.RaisePropertyChanged(nameof(HasStatusMessage));
                break;
            case nameof(OperationMessageState.IsError):
                this.RaisePropertyChanged(nameof(IsStatusMessageError));
                break;
            case nameof(OperationMessageState.IsInformation):
                this.RaisePropertyChanged(nameof(IsStatusMessageInformation));
                break;
            case nameof(OperationMessageState.IsSuccess):
                this.RaisePropertyChanged(nameof(IsStatusMessageSuccess));
                break;
            case nameof(OperationMessageState.HasNonErrorMessage):
                this.RaisePropertyChanged(nameof(HasNonErrorStatusMessage));
                break;
        }

        OnStatusMessageChanged();
    }

    private void HandleInterfaceMotionChanged(object? sender, EventArgs e)
    {
        this.RaisePropertyChanged(nameof(EffectiveInterfaceAnimationsEnabled));
        OnInterfaceMotionChanged();
    }

    private void HandlePreferencesChanged(object? sender, UiPreferencesChangedEventArgs e)
    {
        if (e.LanguageChanged)
        {
            this.RaisePropertyChanged(nameof(CurrentLanguage));
            this.RaisePropertyChanged(nameof(ScrollBarScrollHereLabel));
            this.RaisePropertyChanged(nameof(ScrollBarTopLabel));
            this.RaisePropertyChanged(nameof(ScrollBarBottomLabel));
            this.RaisePropertyChanged(nameof(ScrollBarPageUpLabel));
            this.RaisePropertyChanged(nameof(ScrollBarPageDownLabel));
            this.RaisePropertyChanged(nameof(ScrollBarScrollUpLabel));
            this.RaisePropertyChanged(nameof(ScrollBarScrollDownLabel));
            this.RaisePropertyChanged(nameof(ScrollBarLeftEdgeLabel));
            this.RaisePropertyChanged(nameof(ScrollBarRightEdgeLabel));
            this.RaisePropertyChanged(nameof(ScrollBarPageLeftLabel));
            this.RaisePropertyChanged(nameof(ScrollBarPageRightLabel));
            this.RaisePropertyChanged(nameof(ScrollBarScrollLeftLabel));
            this.RaisePropertyChanged(nameof(ScrollBarScrollRightLabel));
            OperationMessage.Refresh();
            OnLanguageChanged();
        }

        if (e.ThemeChanged)
        {
            this.RaisePropertyChanged(nameof(CurrentThemeId));
            this.RaisePropertyChanged(nameof(CurrentThemeBrightness));
            OnThemeChanged();
        }

        if (e.InterfaceAnimationsChanged)
        {
            this.RaisePropertyChanged(nameof(InterfaceAnimationsEnabled));
        }
    }
}
