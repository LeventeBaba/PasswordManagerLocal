namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// Public historical membership declaration authenticated by the original control author. Delivery
/// uses the exact keys and operation-hash references together with signed predecessor proofs.
/// Receipt-only reconstructed identities never replace live data-plane authorization history.
/// </summary>
public sealed class AccountDeletionKnownMember
{
    private byte[]? _additionOperationHash;
    private byte[]? _removalOperationHash;

    public Guid AuthorizationId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long StartedMembershipEpoch { get; set; }
    public long? EndedMembershipEpoch { get; set; }
    public long MinimumKeyEpoch { get; set; }
    public long? MaximumKeyEpoch { get; set; }
    public byte[] SignPublicKey { get; set; } = [];
    // Match UserMembershipAuthorization: an empty optional hash means absent evidence.
    // Normalize at assignment so direct construction and JSON readback use one representation.
    public byte[]? AdditionOperationHash
    {
        get => _additionOperationHash;
        set => _additionOperationHash = value is { Length: 0 } ? null : value;
    }
    public byte[]? RemovalOperationHash
    {
        get => _removalOperationHash;
        set => _removalOperationHash = value is { Length: 0 } ? null : value;
    }
    public byte[] SignPublicKeyHash { get; set; } = [];
    public Guid? AdditionOperationId { get; set; }
    public Guid? RemovalOperationId { get; set; }
}
