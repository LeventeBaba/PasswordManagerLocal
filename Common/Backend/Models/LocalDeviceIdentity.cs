using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class LocalDeviceIdentity : IntegrityCheckableBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OriginInstanceId { get; set; } = Guid.NewGuid();
    public byte[] AgreementPrivateKeyBlob { get; set; } = [];
    public byte[] SignPrivateKeyBlob { get; set; } = [];
    public byte[] PFXCertificate { get; set; } = [];
    public DeviceType DeviceType { get; set; }
    public bool IsSyncOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<LocalUserDevice> LocalUsers { get; set; } = [];

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.Write(OriginInstanceId);
            hash.WriteBytes(AgreementPrivateKeyBlob);
            hash.WriteBytes(SignPrivateKeyBlob);
            hash.WriteBytes(PFXCertificate);
            hash.Write((byte)DeviceType);
            hash.Write(IsSyncOn);
            hash.Write(CreatedAt);
        });

}
