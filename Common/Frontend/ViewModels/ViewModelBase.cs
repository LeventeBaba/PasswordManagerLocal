using PasswordManagerLocal.Common.Frontend.Exceptions;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
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
        OperationMessage.PropertyChanged += HandleOperationMessageChanged;
    }

    protected UiPreferencesService UiPreferences { get; }
    public FrontendPlatformServices PlatformServices => UiPreferences.PlatformServices;
    protected CancellationToken LifetimeToken => PlatformServices.LifetimeToken;

    protected OperationMessageState OperationMessage { get; } = new();

    public AppLanguage CurrentLanguage => UiPreferences.CurrentLanguage;

    public AppThemeMode CurrentThemeMode => UiPreferences.CurrentThemeMode;

    public string? StatusMessage => OperationMessage.Message;

    public bool HasStatusMessage => OperationMessage.HasMessage;

    public bool IsStatusMessageError => OperationMessage.IsError;

    public bool IsStatusMessageInformation => OperationMessage.IsInformation;

    public bool IsStatusMessageSuccess => OperationMessage.IsSuccess;

    public bool HasNonErrorStatusMessage => OperationMessage.HasNonErrorMessage;

    protected string GetTranslation(string key) => UiPreferences.GetString(key);

    protected void ShowInformationMessage(string message, bool autoDismiss = false) =>
        OperationMessage.ShowInformation(message, autoDismiss);

    protected void ShowSuccessMessage(string message) =>
        OperationMessage.ShowSuccess(message);

    protected void ShowErrorMessage(string message) =>
        OperationMessage.ShowError(message);

    public void ClearStatusMessage() => OperationMessage.Clear();

    public virtual void OnNavigatedFrom() => ClearStatusMessage();

    protected string GetSafeErrorMessage(Exception exception) =>
        exception switch
        {
            InvalidTokenException => GetTranslation("Error_InvalidSession"),
            UserNotFoundException => GetTranslation("Error_InvalidCredentials"),
            UnauthorizedAccessException => GetTranslation("Error_InvalidCredentials"),
            InvalidInputException => GetTranslation("Error_InvalidInput"),
            PasswordNotFoundException => GetTranslation("Error_NotFound"),
            DuplicatePasswordNameException => GetTranslation("Error_DuplicatePasswordName"),
            DuplicatePasswordTagNameException => GetTranslation("Error_DuplicatePasswordTagName"),
            PasswordTagNotFoundException => GetTranslation("Error_NotFound"),
            DuplicateCustomUserColorNameException => GetTranslation("Error_DuplicateCustomColorName"),
            DuplicateCustomUserColorCodeException => GetTranslation("Error_DuplicateCustomColorCode"),
            CustomUserColorNotFoundException => GetTranslation("Error_NotFound"),
            LimitReachedException => GetTranslation("Error_LimitReached"),
            InvalidDataIntegrityException => GetTranslation("Error_DataIntegrity"),
            DeviceIdentityNotInitilaizedException => GetTranslation("Error_DeviceIdentity"),
            DeviceEnrollmentException deviceEnrollmentException => GetDeviceEnrollmentErrorMessage(deviceEnrollmentException),
            DuplicateActiveProfileException => GetTranslation("Error_ProfileAlreadyLoggedIn"),
            OperationCanceledException => GetTranslation("Error_OperationCanceled"),
            _ => GetTranslation("Error_Generic")
        };

    protected string GetDeviceEnrollmentErrorMessage(DeviceEnrollmentException exception)
    {
        var message = GetDeviceEnrollmentErrorMessage(exception.ErrorCode);
        var detail = exception.Message?.Trim();

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

    protected virtual void OnStatusMessageChanged()
    {
    }

    protected override void DisposeManaged()
    {
        UiPreferences.PreferencesChanged -= HandlePreferencesChanged;
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

    private void HandlePreferencesChanged(object? sender, UiPreferencesChangedEventArgs e)
    {
        if (e.LanguageChanged)
            OnLanguageChanged();

        if (e.ThemeChanged)
            OnThemeChanged();
    }
}
