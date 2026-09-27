namespace PasswordManagerLocal.Common.Backend.Models.Projections;

public sealed class GroupWithUserIdsData
{
    public Guid Id { get; init; }
    public byte[] EncryptedPayload { get; init; } = [];
    public DateTimeOffset LastModifiedAt { get; init; }
    public byte[] IntegrityHash { get; set; } = [];
    public List<Guid> UserIds { get; init; } = [];
}
