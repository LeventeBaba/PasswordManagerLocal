using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class LocalUserDevice : IntegrityCheckableBase
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid LocalDeviceIdentityId { get; set; }
    public LocalDeviceIdentity? LocalDeviceIdentity { get; set; }

    public bool IsSyncOn { get; set; } = true;

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(UserId);
            hash.Write(LocalDeviceIdentityId);
            hash.Write(IsSyncOn);
        });

}
