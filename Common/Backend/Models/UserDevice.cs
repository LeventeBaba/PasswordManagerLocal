using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserDevice : IntegrityCheckableBase
{
    public Guid ModelId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public bool IsSyncOn { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;


    public override void GenerateIntegrityHash()
    {
        ModelId = Sync.SyncIdentityUtil.BuildUserDeviceModelId(UserId, DeviceId);
        base.GenerateIntegrityHash();
    }

    public override bool IsIntegrityValid() =>
        ModelId == Sync.SyncIdentityUtil.BuildUserDeviceModelId(UserId, DeviceId) && base.IsIntegrityValid();

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(ModelId);
            hash.Write(UserId);
            hash.Write(DeviceId);
            hash.Write(IsSyncOn);
            hash.Write(IsDeleted);
            hash.Write(DeletedAt);
            hash.Write(LastModifiedAt);
        });

}
