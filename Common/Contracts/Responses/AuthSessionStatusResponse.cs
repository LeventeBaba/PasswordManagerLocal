using PasswordManagerLocal.Common.Contracts.Authentication;

namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class AuthSessionStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public AuthSessionInvalidationReason InvalidationReason { get; set; } = AuthSessionInvalidationReason.None;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
