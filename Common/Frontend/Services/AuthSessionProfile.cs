namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class AuthSessionProfile
{
    public Guid Token { get; init; }
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsRememberMeEnabled { get; init; }
}
