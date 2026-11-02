namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>Local-only recovery data, inside the encrypted database and additionally device protected.</summary>
public sealed class UserRecoveryJournal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public UserRecoveryJournalState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] ProtectedKey { get; set; } = [];
    public byte[] EncryptedPayload { get; set; } = [];
    public byte[] PayloadHash { get; set; } = [];
}
