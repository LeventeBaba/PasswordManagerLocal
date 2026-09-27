using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using System.ComponentModel.DataAnnotations.Schema;

namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Durable, authenticated effective login identity. This is intentionally separate from
/// the canonical encrypted user bundle so a verified pending snapshot can invalidate an
/// old username before the encrypted general-data blob is truly merged.
/// </summary>
public sealed class UserLoginIdentityState
{
    public Guid UserId { get; set; }
    public byte[] UsernameHash { get; set; } = [];
    public byte[] UsernameSalt { get; set; } = [];

    public long VersionPhysicalTimeUnixMilliseconds { get; set; }
    public long VersionLogicalCounter { get; set; }
    public Guid VersionOriginDeviceId { get; set; }
    public Guid VersionOriginInstanceId { get; set; }

    public Guid SourceOriginDeviceId { get; set; }
    public Guid SourceOriginInstanceId { get; set; }
    public long SourceOriginRevision { get; set; }
    public byte[] SourceSnapshotHash { get; set; } = [];
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public UserLoginIdentityStatus Status { get; set; } = UserLoginIdentityStatus.Active;
    public string? StatusReason { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long ConcurrencyVersion { get; set; }

    [NotMapped]
    public SyncVersionStamp Version
    {
        get => new()
        {
            PhysicalTimeUnixMilliseconds = VersionPhysicalTimeUnixMilliseconds,
            LogicalCounter = VersionLogicalCounter,
            OriginDeviceId = VersionOriginDeviceId,
            OriginInstanceId = VersionOriginInstanceId
        };
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            VersionPhysicalTimeUnixMilliseconds = value.PhysicalTimeUnixMilliseconds;
            VersionLogicalCounter = value.LogicalCounter;
            VersionOriginDeviceId = value.OriginDeviceId;
            VersionOriginInstanceId = value.OriginInstanceId;
        }
    }
}
