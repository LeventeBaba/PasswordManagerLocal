using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class DeviceAdditionPayload
{
    public Guid UserId { get; set; }
    public Guid NewDeviceId { get; set; }
    public Guid NewOriginInstanceId { get; set; }
    public long PreviousMembershipEpoch { get; set; }
    public long ResultingMembershipEpoch { get; set; }
    public long KeyEpoch { get; set; }
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public byte[] IntegrityHash { get; set; } = [];
}
