using PasswordManagerLocal.Common.Backend.Security;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class Device : IntegrityCheckableBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public byte[] PublicKey { get; set; } = [];
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] SignPublicKeyHash { get; set; } = [];
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public byte[] LastKnownHash { get; set; } = [];

    public DateTime LastSync { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool IsTrusted { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public int InvalidSyncAttemptCount { get; set; }
    public DateTimeOffset? LastInvalidSyncAttemptAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<UserDevice> UserDevices { get; set; } = [];
    public ICollection<SyncQueueItem> ItemsNeedingSync { get; set; } = [];


    public override void GenerateIntegrityHash()
    {
        SignPublicKeyHash = SignPublicKey.Length == 0 ? [] : Hashing.SHA256Hash(SignPublicKey);
        base.GenerateIntegrityHash();
    }

    public override bool IsIntegrityValid()
    {
        var expectedHash = SignPublicKey.Length == 0 ? [] : Hashing.SHA256Hash(SignPublicKey);
        return Hashing.Verify(SignPublicKeyHash, expectedHash) && base.IsIntegrityValid();
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteBytes(PublicKey);
            hash.WriteBytes(SignPublicKey);
            hash.WriteBytes(SignPublicKeyHash);
            hash.WriteString(TlsCertFingerprint);
            hash.Write((byte)DeviceType);
            hash.WriteBytes(LastKnownHash);
            hash.Write(LastSync);
            hash.Write(LastSeen);
            hash.Write(IsTrusted);
            hash.Write(IsBlocked);
            hash.WriteString(BlockedReason);
            hash.Write(BlockedAt);
            hash.Write(InvalidSyncAttemptCount);
            hash.Write(LastInvalidSyncAttemptAt);
            hash.Write(LastModifiedAt);
        });

}
