namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Durable historical authorization for one exact device installation. The row deliberately has
/// no cascading foreign keys so immutable signed content remains verifiable after current rows are removed.
/// </summary>
public sealed class UserMembershipAuthorization
{
    private byte[]? _additionOperationHash;
    private byte[]? _removalOperationHash;

    public Guid AuthorizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] SignPublicKeyHash { get; set; } = [];
    public byte[] AgreementPublicKeyHash { get; set; } = [];
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public long StartedMembershipEpoch { get; set; }
    public long? EndedMembershipEpoch { get; set; }
    public long MinimumKeyEpoch { get; set; }
    public long? MaximumKeyEpoch { get; set; }
    public bool IsActive { get; set; }
    public bool IsGenesis { get; set; }
    public Guid? AdditionOperationId { get; set; }

    // Normalize empty optional hashes to the absent-evidence representation.
    public byte[]? AdditionOperationHash
    {
        get => _additionOperationHash;
        set => _additionOperationHash = value is { Length: 0 } ? null : value;
    }
    public Guid? RemovalOperationId { get; set; }
    public byte[]? RemovalOperationHash
    {
        get => _removalOperationHash;
        set => _removalOperationHash = value is { Length: 0 } ? null : value;
    }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public long Version { get; set; }
}
