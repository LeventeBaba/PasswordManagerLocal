using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ITokenService
{
    Guid Issue(Guid uid);
    bool Validate(Guid token);
    bool TryGetUid(Guid token, out Guid uid);
    bool TryGetExpiresAtUtc(Guid token, out DateTimeOffset expiresAtUtc);
    Guid GetUidOrThrow(Guid token);
    IReadOnlyList<Guid> ListTokensByUid(Guid uid);
    bool Revoke(Guid token);
    bool Revoke(Guid token, AuthSessionInvalidationReason reason);
    bool TryGetInvalidationReason(Guid token, out AuthSessionInvalidationReason reason);
    int PurgeExpired();
    void RevokeAll();
}