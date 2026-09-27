namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IAuthService :
    IUserRegistrationService,
    IUserLoginService,
    IAuthSessionService,
    IMasterPasswordRotationService,
    ICredentialVerificationService
{
}
