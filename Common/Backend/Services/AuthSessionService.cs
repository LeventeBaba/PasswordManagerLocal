using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class AuthSessionService : IAuthSessionService, IAuthenticatedSessionIssuer
{
    private readonly IUserDataReaderService _userDataReader;
    private readonly ITokenService _tokens;
    private readonly IDataCachingService _cache;
    private readonly IKeyVaultService _keys;

    public AuthSessionService(
        IUserDataReaderService userDataReader,
        ITokenService tokens,
        IDataCachingService cache,
        IKeyVaultService keys)
    {
        _userDataReader = userDataReader;
        _tokens = tokens;
        _cache = cache;
        _keys = keys;
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
                _keys.SetUserBlobKeys(newToken, bundle.UserData);
                _cache.SetUserDataBundle(newToken, bundle);
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


    public async Task RefreshSyncedUserSessionsAsync(User user, CancellationToken ct = default)
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
                var bundle = await _userDataReader.GetAndVerifyUserDataBundleAsync(user, key, ct);
                _keys.SetUserBlobKeys(token, bundle.UserData);
                _cache.SetUserDataBundle(token, bundle);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                InvalidateToken(token, AuthSessionInvalidationReason.ProfilePasswordChanged);
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
