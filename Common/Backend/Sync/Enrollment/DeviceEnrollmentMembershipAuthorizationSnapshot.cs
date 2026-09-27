using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentMembershipAuthorizationSnapshot
{
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
    public byte[]? AdditionOperationHash { get; set; }
    public Guid? RemovalOperationId { get; set; }
    public byte[]? RemovalOperationHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
}
