namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class PasswordTagInfoResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}
