using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

/// <summary>
/// Immutable identity of one logical encrypted-data mutation. Snapshot revisions are intentionally separate.
/// </summary>
public sealed record SyncVersionStamp
{
    public long PhysicalTimeUnixMilliseconds { get; init; }
    public long LogicalCounter { get; init; }
    public Guid OriginDeviceId { get; init; }
    public Guid OriginInstanceId { get; init; }

    public bool IsValid =>
        PhysicalTimeUnixMilliseconds > 0 &&
        LogicalCounter >= 0 &&
        OriginDeviceId != Guid.Empty &&
        OriginInstanceId != Guid.Empty;

    public void WriteTo(Sha256HashBuilder hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        hash.Write(PhysicalTimeUnixMilliseconds);
        hash.Write(LogicalCounter);
        hash.Write(OriginDeviceId);
        hash.Write(OriginInstanceId);
    }
}
