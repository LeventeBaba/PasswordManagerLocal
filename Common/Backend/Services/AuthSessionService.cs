using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class AuthSessionService : IAuthSessionService, IAuthenticatedSessionIssuer
{
    private readonly IUserRepository _users;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUserDataReaderService _userDataReader;
    private readonly ITokenService _tokens;
    private readonly IDataCachingService _cache;
    private readonly IKeyVaultService _keys;

    public AuthSessionService(
        IUserDataReaderService userDataReader,
        ITokenService tokens,
        IDataCachingService cache,
        IKeyVaultService keys,
        IUserRepository users,
        IUserLifecycleCoordinator lifecycle)
    {
        _userDataReader = userDataReader;
        _tokens = tokens;
        _cache = cache;
        _keys = keys;
        _users = users;
        _lifecycle = lifecycle;
    }

    public Guid IssueAuthenticatedSession(Guid userId, EncryptionKey key, UserDataBundle bundle)
    {
        var token = _tokens.Issue(userId);
        _keys.SetUserKey(token, key);
        _keys.SetUserBlobKeys(token, bundle.UserData);
        _cache.SetUserDataBundle(token, bundle);
        return token;
    }

    public Task<Guid> RenewSessionAsync(Guid token, CancellationToken ct = default)
    {
        if (!_tokens.TryGetUid(token, out var uid))
            throw new InvalidTokenException();

        if (!_keys.TryGetEncryptionKey(token, out var key))
        {
            InvalidateToken(token, AuthSessionInvalidationReason.Expired);
            throw new InvalidTokenException();
        }

        try
        {
            var newToken = _tokens.Issue(uid);
            _keys.SetUserKey(newToken, key);

            if (_cache.TryGetUserDataBundle(token, out var bundle) && bundle is not null)
            {
                using (bundle)
                {
                    _keys.SetUserBlobKeys(newToken, bundle.UserData);
                    _cache.SetUserDataBundle(newToken, bundle);
                }
            }

            InvalidateToken(token, AuthSessionInvalidationReason.LoggedOut);
            return Task.FromResult(newToken);
        }
        finally
        {
            key.Dispose();
        }
    }


    public void Logout(Guid token)
    {
        if (!_tokens.Validate(token))
            throw new InvalidTokenException();

        InvalidateToken(token, AuthSessionInvalidationReason.LoggedOut);
    }


    public void LogoutUser(Guid uid) =>
        LogoutUser(uid, AuthSessionInvalidationReason.LoggedOut);


    public void LogoutUser(Guid uid, AuthSessionInvalidationReason reason)
    {
        foreach (var token in _tokens.ListTokensByUid(uid))
            InvalidateToken(token, reason);
    }


    public AuthSessionStatusResponse GetSessionStatus(Guid token)
    {
        if (_tokens.TryGetUid(token, out _) && _tokens.TryGetExpiresAtUtc(token, out var expiresAtUtc))
        {
            return new AuthSessionStatusResponse
            {
                IsAuthenticated = true,
                InvalidationReason = AuthSessionInvalidationReason.None,
                ExpiresAtUtc = expiresAtUtc
            };
        }

        var reason = _tokens.TryGetInvalidationReason(token, out var foundReason)
            ? foundReason
            : AuthSessionInvalidationReason.Expired;

        return new AuthSessionStatusResponse
        {
            IsAuthenticated = false,
            InvalidationReason = reason
        };
    }


    public Task RefreshSyncedUserSessionsAsync(User user, CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(user.UId, async innerCt =>
        {
            // A delayed refresh must reload the latest committed generation, not install its
            // caller's old tracked entity. Read/decrypt/install are serialized with mutations.
            var current = await _users.GetByIdAsNoTrackingAsync(user.UId, innerCt);
            if (current is not null) await RefreshCurrentSessionsAsync(current, innerCt);
        }, ct);

    private async Task RefreshCurrentSessionsAsync(User user, CancellationToken ct)
    {
        foreach (var token in _tokens.ListTokensByUid(user.UId))
        {
            if (!_keys.TryGetEncryptionKey(token, out var key))
            {
                InvalidateToken(token, AuthSessionInvalidationReason.Expired);
                continue;
            }

            try
            {
                using var bundle = await _userDataReader.GetAndVerifyUserDataBundleAsync(user, key, ct);
                _keys.SetUserBlobKeys(token, bundle.UserData);
                _cache.SetUserDataBundle(token, bundle);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is PasswordManagerLocal.Common.Contracts.Errors.InvalidDataIntegrityException or
                InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                BackendDebugLog.Warning($"Session verification failed. User={user.UId}, Failure={ex.GetType().Name}.", category: "Authentication");
                InvalidateToken(token, AuthSessionInvalidationReason.LocalDataVerificationFailed);
            }
            catch (Exception ex)
            {
                // A local I/O/service failure is not proof that the password or vault changed.
                _cache.InvalidateToken(token);
                BackendDebugLog.Warning($"Session refresh deferred. User={user.UId}, Failure={ex.GetType().Name}.", category: "Authentication");
            }
            finally
            {
                key.Dispose();
            }
        }
    }


    private void InvalidateToken(Guid token, AuthSessionInvalidationReason reason)
    {
        _cache.InvalidateToken(token);
        _keys.InvalidateToken(token);
        _tokens.Revoke(token, reason);
    }
}
