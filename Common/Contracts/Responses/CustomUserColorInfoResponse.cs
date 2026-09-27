namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class CustomUserColorInfoResponse
{
    public Guid Id { get; set; }
    public string? ColorName { get; set; }
    public string ColorCode { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}
