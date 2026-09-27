using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSyncKeyResolverService : IUserSyncKeyResolverService
{
    private readonly IInteractiveSessionStateService _interactiveSessionState;
    private readonly IKeyProtector _protector;

    public UserSyncKeyResolverService(
        IInteractiveSessionStateService interactiveSessionState,
        IKeyProtector protector)
    {
        _interactiveSessionState = interactiveSessionState
            ?? throw new ArgumentNullException(nameof(interactiveSessionState));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public bool TryResolve(User user, out EncryptionKey? key) =>
        TryResolve(user, out key, out _);

    public bool TryResolve(User user, out EncryptionKey? key, out UserSyncKeyConfidence confidence)
    {
        if (_interactiveSessionState.TryGetUserEncryptionKey(user.UId, out key) && key is not null)
        {
            confidence = UserSyncKeyConfidence.AuthenticatedSession;
            return true;
        }

        key = null;
        confidence = UserSyncKeyConfidence.UnconfirmedPassword;
        if (user.SavedKey is null || user.SavedKey.Length == 0)
            return false;

        byte[]? raw = null;
        try
        {
            raw = _protector.Unprotect(user.SavedKey);
            key = EncryptionKey.FromRaw(raw);
            confidence = UserSyncKeyConfidence.RememberMe;
            return true;
        }
        catch (KeyProtectorUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key = null;
            confidence = UserSyncKeyConfidence.UnconfirmedPassword;
            return false;
        }
        finally
        {
            if (raw is not null)
                CryptographicOperations.ZeroMemory(raw);
        }
    }
}
