using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Validation;
using PasswordManagerLocal.Common.Frontend.Helpers;
using PasswordManagerLocal.Common.Frontend.Security;
using PasswordManagerLocal.Common.Frontend.Services;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Auth;

public sealed class RegistrationViewModel : ViewModelBase
{
    private readonly IEndpoints _endpoints;
    private readonly PasswordStrengthEstimator _passwordStrengthEstimator = new();
    private readonly Action _navigateToLogin;
    private readonly Func<Guid, Task> _onAuthenticationSucceededAsync;
    private Func<Task>? _navigateBackAsync;

    private string _username = string.Empty;
    private string _firstName = string.Empty;
    private string _lastName = string.Empty;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private bool _rememberMe;
    private bool _isPasswordVisible;
    private bool _isConfirmPasswordVisible;
    private bool _isBusy;
    private bool _isBackendInitialized;
    private int _passwordStrength;

    private bool _usernameTouched;
    private bool _firstNameTouched;
    private bool _lastNameTouched;
    private bool _emailTouched;
    private bool _passwordTouched;
    private bool _confirmPasswordTouched;

    private string? _usernameServerValidationMessage;
    private string? _firstNameServerValidationMessage;
    private string? _lastNameServerValidationMessage;
    private string? _emailServerValidationMessage;
    private string? _passwordServerValidationMessage;

    public RegistrationViewModel(
        UiPreferencesService uiPreferences,
        IEndpoints endpoints,
        Action navigateToLogin,
        Func<Guid, Task> onAuthenticationSucceededAsync)
        : base(uiPreferences)
    {
        _endpoints = endpoints;
        _navigateToLogin = navigateToLogin;
        _onAuthenticationSucceededAsync = onAuthenticationSucceededAsync;

        RegisterCommand = Own(ReactiveCommand.CreateFromTask(RegisterAsync));
        NavigateToLoginCommand = Own(ReactiveCommand.Create(_navigateToLogin));
        NavigateBackCommand = Own(ReactiveCommand.CreateFromTask(NavigateBackAsync));
        TogglePasswordVisibilityCommand = Own(ReactiveCommand.Create(TogglePasswordVisibility));
        ToggleConfirmPasswordVisibilityCommand = Own(ReactiveCommand.Create(ToggleConfirmPasswordVisibility));
    }

    public string Username
    {
        get => _username;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_username, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _username, value);
            _usernameTouched = true;
            _usernameServerValidationMessage = null;
            RefreshPasswordStrength();
            RaiseRegistrationValidationChanged();
        }
    }

    public string FirstName
    {
        get => _firstName;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_firstName, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _firstName, value);
            _firstNameTouched = true;
            _firstNameServerValidationMessage = null;
            RefreshPasswordStrength();
            RaiseRegistrationValidationChanged();
        }
    }

    public string LastName
    {
        get => _lastName;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_lastName, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _lastName, value);
            _lastNameTouched = true;
            _lastNameServerValidationMessage = null;
            RefreshPasswordStrength();
            RaiseRegistrationValidationChanged();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_email, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _email, value);
            _emailTouched = true;
            _emailServerValidationMessage = null;
            RefreshPasswordStrength();
            RaiseRegistrationValidationChanged();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_password, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _password, value);
            _passwordTouched = true;
            _passwordServerValidationMessage = null;
            RefreshPasswordStrength();
            RaiseRegistrationValidationChanged();
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_confirmPassword, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _confirmPassword, value);
            _confirmPasswordTouched = true;
            RaiseRegistrationValidationChanged();
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

    public bool IsConfirmPasswordVisible
    {
        get => _isConfirmPasswordVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isConfirmPasswordVisible, value);
            this.RaisePropertyChanged(nameof(ConfirmPasswordMaskCharacter));
            this.RaisePropertyChanged(nameof(ConfirmPasswordVisibilityToggleText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanRegister));
        }
    }

    public bool IsBackendInitialized
    {
        get => _isBackendInitialized;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBackendInitialized, value);
            this.RaisePropertyChanged(nameof(CanRegister));
        }
    }

    public bool IsBackButtonVisible => true;

    public char PasswordMaskCharacter => IsPasswordVisible ? '\0' : '●';

    public char ConfirmPasswordMaskCharacter => IsConfirmPasswordVisible ? '\0' : '●';

    public int PasswordStrength => _passwordStrength;

    public int UsernameMaxLength => DataLengthConstants.UsernameMaxLength;

    public int FirstNameMaxLength => DataLengthConstants.FirstNameMaxLength;

    public int LastNameMaxLength => DataLengthConstants.LastNameMaxLength;

    public int EmailMaxLength => DataLengthConstants.EmailMaxLength;

    public string? UsernameValidationMessage => GetUsernameValidationMessage();

    public string? FirstNameValidationMessage => GetFirstNameValidationMessage();

    public string? LastNameValidationMessage => GetLastNameValidationMessage();

    public string? EmailValidationMessage => GetEmailValidationMessage();

    public string? PasswordValidationMessage => GetPasswordValidationMessage();

    public string? ConfirmPasswordValidationMessage => GetConfirmPasswordValidationMessage();

    public bool HasUsernameValidationError => UsernameValidationMessage is not null;

    public bool HasFirstNameValidationError => FirstNameValidationMessage is not null;

    public bool HasLastNameValidationError => LastNameValidationMessage is not null;

    public bool HasEmailValidationError => EmailValidationMessage is not null;

    public bool HasPasswordValidationError => PasswordValidationMessage is not null;

    public bool HasConfirmPasswordValidationError => ConfirmPasswordValidationMessage is not null;

    public bool CanRegister =>
        IsBackendInitialized &&
        !IsBusy &&
        IsUsernameLocallyValid() &&
        IsFirstNameLocallyValid() &&
        IsLastNameLocallyValid() &&
        IsEmailLocallyValid() &&
        IsPasswordLocallyValid() &&
        IsConfirmPasswordLocallyValid() &&
        !HasServerValidationErrors;

    public ReactiveCommand<RxVoid, RxVoid> RegisterCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> NavigateToLoginCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> NavigateBackCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ToggleConfirmPasswordVisibilityCommand { get; }

    public string Title => GetTranslation("Register_Title");

    public string BackLabel => GetTranslation("Common_Back");

    public string Subtitle => GetTranslation("Register_Subtitle");

    public string UsernameLabel => GetTranslation("Register_Username_Label");

    public string FirstNameLabel => GetTranslation("Register_FirstName_Label");

    public string LastNameLabel => GetTranslation("Register_LastName_Label");

    public string EmailLabel => GetTranslation("Register_Email_Label");

    public string PasswordLabel => GetTranslation("Register_Password_Label");

    public string ConfirmPasswordLabel => GetTranslation("Register_ConfirmPassword_Label");

    public string RememberMeLabel => GetTranslation("Register_RememberMe_Label");

    public string RememberMeOnLabel => GetTranslation("Common_On");

    public string RememberMeOffLabel => GetTranslation("Common_Off");

    public string RegisterButtonLabel => GetTranslation("Register_Button");

    public string AlreadyHaveAccountText => GetTranslation("Register_AlreadyHaveAccount_Text");

    public string NavigateToLoginLabel => GetTranslation("Register_NavigateToLogin_Button");

    public string UsernamePlaceholder => GetTranslation("Register_Username_Placeholder");

    public string FirstNamePlaceholder => GetTranslation("Register_FirstName_Placeholder");

    public string LastNamePlaceholder => GetTranslation("Register_LastName_Placeholder");

    public string EmailPlaceholder => GetTranslation("Register_Email_Placeholder");

    public string PasswordPlaceholder => GetTranslation("Register_Password_Placeholder");

    public string ConfirmPasswordPlaceholder => GetTranslation("Register_ConfirmPassword_Placeholder");

    public string PasswordVisibilityToggleText => GetTranslation(IsPasswordVisible ? "Common_Hide" : "Common_Show");

    public string ConfirmPasswordVisibilityToggleText => GetTranslation(IsConfirmPasswordVisible ? "Common_Hide" : "Common_Show");

    public string BusyText => GetTranslation("Common_Loading");

    public string PasswordStrengthLabel => GetTranslation("PasswordStrength_Label");

    public string PasswordStrengthInfoTitle => GetTranslation("PasswordStrength_Info_Title");

    public string PasswordStrengthInfoBody => GetTranslation("PasswordStrength_Info_Body");

    public string PasswordStrengthInfoAccessibleLabel => GetTranslation("PasswordStrength_Info_AccessibleLabel");

    protected override void OnLanguageChanged()
    {
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(BackLabel));
        this.RaisePropertyChanged(nameof(Subtitle));
        this.RaisePropertyChanged(nameof(UsernameLabel));
        this.RaisePropertyChanged(nameof(FirstNameLabel));
        this.RaisePropertyChanged(nameof(LastNameLabel));
        this.RaisePropertyChanged(nameof(EmailLabel));
        this.RaisePropertyChanged(nameof(PasswordLabel));
        this.RaisePropertyChanged(nameof(ConfirmPasswordLabel));
        this.RaisePropertyChanged(nameof(RememberMeLabel));
        this.RaisePropertyChanged(nameof(RememberMeOnLabel));
        this.RaisePropertyChanged(nameof(RememberMeOffLabel));
        this.RaisePropertyChanged(nameof(RegisterButtonLabel));
        this.RaisePropertyChanged(nameof(AlreadyHaveAccountText));
        this.RaisePropertyChanged(nameof(NavigateToLoginLabel));
        this.RaisePropertyChanged(nameof(UsernamePlaceholder));
        this.RaisePropertyChanged(nameof(FirstNamePlaceholder));
        this.RaisePropertyChanged(nameof(LastNamePlaceholder));
        this.RaisePropertyChanged(nameof(EmailPlaceholder));
        this.RaisePropertyChanged(nameof(PasswordPlaceholder));
        this.RaisePropertyChanged(nameof(ConfirmPasswordPlaceholder));
        this.RaisePropertyChanged(nameof(PasswordVisibilityToggleText));
        this.RaisePropertyChanged(nameof(ConfirmPasswordVisibilityToggleText));
        this.RaisePropertyChanged(nameof(BusyText));
        this.RaisePropertyChanged(nameof(PasswordStrengthLabel));
        this.RaisePropertyChanged(nameof(PasswordStrengthInfoTitle));
        this.RaisePropertyChanged(nameof(PasswordStrengthInfoBody));
        this.RaisePropertyChanged(nameof(PasswordStrengthInfoAccessibleLabel));

        RefreshLocalizedServerValidationMessages();
        RaiseRegistrationValidationChanged();
    }

    public void Reset()
    {
        Username = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
        Email = string.Empty;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        RememberMe = false;
        IsPasswordVisible = false;
        IsConfirmPasswordVisible = false;

        ResetValidationInteractionState();
        ClearStatusMessage();
    }

    internal void SetBackendInitialized(bool isInitialized) =>
        IsBackendInitialized = isInitialized;

    public void SetBackNavigation(Func<Task>? navigateBackAsync) =>
        _navigateBackAsync = navigateBackAsync;

    private async Task NavigateBackAsync()
    {
        if (IsBusy)
            return;

        if (_navigateBackAsync is not null)
        {
            await _navigateBackAsync();
            return;
        }

        _navigateToLogin();
    }

    private async Task RegisterAsync()
    {
        if (!CanRegister)
        {
            MarkAllFieldsTouched();
            RaiseRegistrationValidationChanged();
            return;
        }

        ClearStatusMessage();
        ClearServerValidationMessages();

        var passwordHash = SecretTransform.HashPassword(Password);
        var registrationCommitted = false;

        try
        {
            IsBusy = true;
            var token = await _endpoints.RegisterAsync(CreateRegistrationRequest(passwordHash));
            registrationCommitted = true;

            await TryInitializeDefaultCustomColorsAsync(token);
            await _onAuthenticationSucceededAsync(token);
            Reset();
        }
        catch (InvalidInputException ex) when (!registrationCommitted)
        {
            MarkAllFieldsTouched();
            if (!ApplyBackendValidationErrors(ex))
                ShowErrorMessage(GetSafeErrorMessage(ex));
        }
        catch (Exception ex)
        {
            if (registrationCommitted)
            {
                ShowErrorMessage(GetTranslation("Register_AccountCreated_PostSetupFailed"));
            }
            else
            {
                ShowErrorMessage(GetSafeErrorMessage(ex));
            }
        }
        finally
        {
            IsBusy = false;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordHash);

            if (registrationCommitted)
                ClearCommittedPasswordFields();
        }
    }

    private async Task TryInitializeDefaultCustomColorsAsync(Guid token)
    {
        try
        {
            await _endpoints.AddCustomUserColorsAsync(token, CreateDefaultCustomColorRequests());
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Default colors are convenience data. Their initialization must never turn an
            // already-created account into an apparent registration failure.
        }
    }

    private void ClearCommittedPasswordFields()
    {
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        _passwordTouched = false;
        _confirmPasswordTouched = false;
        _passwordServerValidationMessage = null;
        RaiseRegistrationValidationChanged();
    }

    private bool ApplyBackendValidationErrors(InvalidInputException exception)
    {
        if (exception.Errors.Count == 0)
            return false;

        var applied = false;
        foreach (var error in exception.Errors)
        {
            switch (error)
            {
                case RegistrationValidationErrors.UsernameInvalid:
                    _usernameTouched = true;
                    _usernameServerValidationMessage = GetTranslation("Validation_Username_Invalid");
                    applied = true;
                    break;
                case RegistrationValidationErrors.UsernameUnavailable:
                    _usernameTouched = true;
                    _usernameServerValidationMessage = GetTranslation("Validation_Username_Unavailable");
                    applied = true;
                    break;
                case RegistrationValidationErrors.UsernameAvailabilityIndeterminate:
                    _usernameTouched = true;
                    _usernameServerValidationMessage = GetTranslation("Validation_Username_AvailabilityIndeterminate");
                    applied = true;
                    break;
                case "FirstName":
                    _firstNameTouched = true;
                    _firstNameServerValidationMessage = GetTranslation("Validation_FirstName_Invalid");
                    applied = true;
                    break;
                case "LastName":
                    _lastNameTouched = true;
                    _lastNameServerValidationMessage = GetTranslation("Validation_LastName_Invalid");
                    applied = true;
                    break;
                case "Email":
                    _emailTouched = true;
                    _emailServerValidationMessage = GetTranslation("Validation_Email_Invalid");
                    applied = true;
                    break;
                case "Password":
                    _passwordTouched = true;
                    _passwordServerValidationMessage = GetTranslation("Validation_RegisterPassword_Invalid");
                    applied = true;
                    break;
            }
        }

        if (applied)
            RaiseRegistrationValidationChanged();

        return applied;
    }

    private string? GetUsernameValidationMessage()
    {
        if (_usernameServerValidationMessage is not null)
            return _usernameServerValidationMessage;
        if (!_usernameTouched && Username.Length == 0)
            return null;
        if (string.IsNullOrWhiteSpace(Username))
            return GetTranslation("Validation_Username_Required");
        return IsUsernameLocallyValid()
            ? null
            : GetTranslation("Validation_Username_Invalid");
    }

    private string? GetFirstNameValidationMessage()
    {
        if (_firstNameServerValidationMessage is not null)
            return _firstNameServerValidationMessage;
        if (!_firstNameTouched && FirstName.Length == 0)
            return null;
        if (string.IsNullOrWhiteSpace(FirstName))
            return GetTranslation("Validation_FirstName_Required");
        return IsFirstNameLocallyValid()
            ? null
            : GetTranslation("Validation_FirstName_Invalid");
    }

    private string? GetLastNameValidationMessage()
    {
        if (_lastNameServerValidationMessage is not null)
            return _lastNameServerValidationMessage;
        if (!_lastNameTouched && LastName.Length == 0)
            return null;
        if (string.IsNullOrWhiteSpace(LastName))
            return GetTranslation("Validation_LastName_Required");
        return IsLastNameLocallyValid()
            ? null
            : GetTranslation("Validation_LastName_Invalid");
    }

    private string? GetEmailValidationMessage()
    {
        if (_emailServerValidationMessage is not null)
            return _emailServerValidationMessage;
        if (!_emailTouched && Email.Length == 0)
            return null;
        if (string.IsNullOrWhiteSpace(Email))
            return GetTranslation("Validation_Email_Required");
        return IsEmailLocallyValid()
            ? null
            : GetTranslation("Validation_Email_Invalid");
    }

    private string? GetPasswordValidationMessage()
    {
        if (_passwordServerValidationMessage is not null)
            return _passwordServerValidationMessage;
        if (!_passwordTouched && Password.Length == 0)
            return null;
        return IsPasswordLocallyValid()
            ? null
            : GetTranslation("Validation_RegisterPassword_Required");
    }

    private string? GetConfirmPasswordValidationMessage()
    {
        if (!_confirmPasswordTouched && ConfirmPassword.Length == 0)
            return null;
        if (string.IsNullOrWhiteSpace(ConfirmPassword))
            return GetTranslation("Validation_RegisterConfirmPassword_Required");
        return string.Equals(Password, ConfirmPassword, StringComparison.Ordinal)
            ? null
            : GetTranslation("Validation_RegisterPassword_Mismatch");
    }

    private bool IsUsernameLocallyValid() =>
        DataValidation.IsValidUsername(Username.Trim());

    private bool IsFirstNameLocallyValid() =>
        DataValidation.IsValidFirstName(FirstName.Trim());

    private bool IsLastNameLocallyValid() =>
        DataValidation.IsValidLastName(LastName.Trim());

    private bool IsEmailLocallyValid() =>
        DataValidation.IsValidEmail(Email.Trim());

    private bool IsPasswordLocallyValid() =>
        !string.IsNullOrWhiteSpace(Password);

    private bool IsConfirmPasswordLocallyValid() =>
        !string.IsNullOrWhiteSpace(ConfirmPassword) &&
        string.Equals(Password, ConfirmPassword, StringComparison.Ordinal);

    private bool HasServerValidationErrors =>
        _usernameServerValidationMessage is not null ||
        _firstNameServerValidationMessage is not null ||
        _lastNameServerValidationMessage is not null ||
        _emailServerValidationMessage is not null ||
        _passwordServerValidationMessage is not null;

    private void MarkAllFieldsTouched()
    {
        _usernameTouched = true;
        _firstNameTouched = true;
        _lastNameTouched = true;
        _emailTouched = true;
        _passwordTouched = true;
        _confirmPasswordTouched = true;
    }

    private void ResetValidationInteractionState()
    {
        _usernameTouched = false;
        _firstNameTouched = false;
        _lastNameTouched = false;
        _emailTouched = false;
        _passwordTouched = false;
        _confirmPasswordTouched = false;
        ClearServerValidationMessages();
        RaiseRegistrationValidationChanged();
    }

    private void ClearServerValidationMessages()
    {
        _usernameServerValidationMessage = null;
        _firstNameServerValidationMessage = null;
        _lastNameServerValidationMessage = null;
        _emailServerValidationMessage = null;
        _passwordServerValidationMessage = null;
    }

    private void RefreshLocalizedServerValidationMessages()
    {
        if (_usernameServerValidationMessage is not null)
            _usernameServerValidationMessage = GetTranslation("Validation_Username_Unavailable");
        if (_firstNameServerValidationMessage is not null)
            _firstNameServerValidationMessage = GetTranslation("Validation_FirstName_Invalid");
        if (_lastNameServerValidationMessage is not null)
            _lastNameServerValidationMessage = GetTranslation("Validation_LastName_Invalid");
        if (_emailServerValidationMessage is not null)
            _emailServerValidationMessage = GetTranslation("Validation_Email_Invalid");
        if (_passwordServerValidationMessage is not null)
            _passwordServerValidationMessage = GetTranslation("Validation_RegisterPassword_Invalid");
    }

    private void RaiseRegistrationValidationChanged()
    {
        this.RaisePropertyChanged(nameof(CanRegister));
        this.RaisePropertyChanged(nameof(UsernameValidationMessage));
        this.RaisePropertyChanged(nameof(FirstNameValidationMessage));
        this.RaisePropertyChanged(nameof(LastNameValidationMessage));
        this.RaisePropertyChanged(nameof(EmailValidationMessage));
        this.RaisePropertyChanged(nameof(PasswordValidationMessage));
        this.RaisePropertyChanged(nameof(ConfirmPasswordValidationMessage));
        this.RaisePropertyChanged(nameof(HasUsernameValidationError));
        this.RaisePropertyChanged(nameof(HasFirstNameValidationError));
        this.RaisePropertyChanged(nameof(HasLastNameValidationError));
        this.RaisePropertyChanged(nameof(HasEmailValidationError));
        this.RaisePropertyChanged(nameof(HasPasswordValidationError));
        this.RaisePropertyChanged(nameof(HasConfirmPasswordValidationError));
    }

    private void RefreshPasswordStrength()
    {
        var result = _passwordStrengthEstimator.Evaluate(
            Password,
            [Username, FirstName, LastName, Email]);

        if (_passwordStrength == result.Score)
            return;

        _passwordStrength = result.Score;
        this.RaisePropertyChanged(nameof(PasswordStrength));
    }

    private RegistrationRequest CreateRegistrationRequest(byte[] passwordHash) =>
        new()
        {
            Username = Username.Trim(),
            Password = passwordHash,
            FirstName = FirstName.Trim(),
            LastName = LastName.Trim(),
            Email = Email.Trim(),
            RememberMe = RememberMe
        };

    private IReadOnlyList<NewCustomUserColorRequest> CreateDefaultCustomColorRequests() =>
    [
        new() { ColorName = GetTranslation("Passwords_Color_Gold"), ColorCode = "#FFFFD700" },
        new() { ColorName = GetTranslation("Passwords_Color_Blue"), ColorCode = "#FF3B82F6" },
        new() { ColorName = GetTranslation("Passwords_Color_Green"), ColorCode = "#FF22C55E" },
        new() { ColorName = GetTranslation("Passwords_Color_Red"), ColorCode = "#FFEF4444" },
        new() { ColorName = GetTranslation("Passwords_Color_Purple"), ColorCode = "#FFA855F7" },
        new() { ColorName = GetTranslation("Passwords_Color_Orange"), ColorCode = "#FFF97316" },
        new() { ColorName = GetTranslation("Passwords_Color_Gray"), ColorCode = "#FF94A3B8" }
    ];

    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    private void ToggleConfirmPasswordVisibility() => IsConfirmPasswordVisible = !IsConfirmPasswordVisible;

    protected override void DisposeManaged()
    {
        Reset();
        base.DisposeManaged();
    }
}
