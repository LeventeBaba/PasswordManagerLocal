using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class AuthService : IAuthService
{
    private readonly IUserRegistrationService _registration;
    private readonly IUserLoginService _login;
    private readonly IAuthSessionService _sessions;
    private readonly IMasterPasswordRotationService _passwordRotation;
    private readonly ICredentialVerificationService _credentials;

    public AuthService(
        IUserRegistrationService registration,
        IUserLoginService login,
        IAuthSessionService sessions,
        IMasterPasswordRotationService passwordRotation,
        ICredentialVerificationService credentials)
    {
        _registration = registration;
        _login = login;
        _sessions = sessions;
        _passwordRotation = passwordRotation;
        _credentials = credentials;
    }

    public Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default) =>
        _registration.RegisterAsync(request, ct);

    public Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        _login.LoginAsync(request, ct);

    public Task<Guid> RenewSessionAsync(Guid token, CancellationToken ct = default) =>
        _sessions.RenewSessionAsync(token, ct);

    public void Logout(Guid token) =>
        _sessions.Logout(token);

    public void LogoutUser(Guid uid) =>
        _sessions.LogoutUser(uid);

    public void LogoutUser(Guid uid, AuthSessionInvalidationReason reason) =>
        _sessions.LogoutUser(uid, reason);

    public AuthSessionStatusResponse GetSessionStatus(Guid token) =>
        _sessions.GetSessionStatus(token);

    public Task RefreshSyncedUserSessionsAsync(User user, CancellationToken ct = default) =>
        _sessions.RefreshSyncedUserSessionsAsync(user, ct);

    public Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default) =>
        _passwordRotation.ChangeMasterPasswordAsync(request, ct);

    public bool IsPasswordValid(Guid token, byte[] password, byte[] salt) =>
        _credentials.IsPasswordValid(token, password, salt);

    public bool TryGetActiveUserEncryptionKey(Guid uid, out EncryptionKey? key) =>
        _credentials.TryGetActiveUserEncryptionKey(uid, out key);
}
