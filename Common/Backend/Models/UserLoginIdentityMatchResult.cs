namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record UserLoginIdentityMatchResult(
    UserLoginIdentityMatchState State,
    Guid? UserId = null,
    UserLoginIdentityState? Projection = null,
    string? Diagnostic = null);
