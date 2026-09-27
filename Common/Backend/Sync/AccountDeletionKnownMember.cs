namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// Immutable membership-history summary authenticated by an account-deletion operation. It is not
/// used as a substitute for locally retained authorization history; it records which installations
/// the origin knew were relevant when deletion was signed.
/// </summary>
public sealed class AccountDeletionKnownMember
{
    public Guid AuthorizationId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long StartedMembershipEpoch { get; set; }
    public long? EndedMembershipEpoch { get; set; }
    public long MinimumKeyEpoch { get; set; }
    public long? MaximumKeyEpoch { get; set; }
    public byte[] SignPublicKeyHash { get; set; } = [];
    public Guid? AdditionOperationId { get; set; }
    public Guid? RemovalOperationId { get; set; }
}
