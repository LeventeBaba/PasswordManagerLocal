using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Applies the only generation-safe post-recovery session transition available in the current
/// process model. Cached bundles are mutable and carry no immutable canonical-generation token,
/// so refreshing the cache cannot prevent an already-held stale bundle from being written later.
/// Existing sessions are therefore revoked after recovery; login/Remember Me may create a fresh
/// session from the committed canonical generation.
/// </summary>
public sealed class UserRecoverySessionService : IUserRecoverySessionService
{
    private readonly ITokenService _tokens;
    private readonly IKeyVaultService _keys;
    private readonly IDataCachingService _cache;

    public UserRecoverySessionService(
        ITokenService tokens,
        IKeyVaultService keys,
        IDataCachingService cache)
    {
        _tokens = tokens;
        _keys = keys;
        _cache = cache;
    }

    public Task RefreshOrInvalidateAsync(User user, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var token in _tokens.ListTokensByUid(user.UId))
        {
            _cache.InvalidateToken(token);
            _keys.InvalidateToken(token);
            _tokens.Revoke(token, AuthSessionInvalidationReason.CanonicalRecovered);
        }

        return Task.CompletedTask;
    }
}
