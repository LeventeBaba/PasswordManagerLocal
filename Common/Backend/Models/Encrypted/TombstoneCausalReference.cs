using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

/// <summary>
/// Authenticated causal identity of the first canonical snapshot revision expected to contain
/// a deletion mutation. The item-level <see cref="SyncVersionStamp"/> remains the immutable
/// deletion identity; this reference bridges that mutation to signed snapshot coverage.
/// </summary>
public sealed record TombstoneCausalReference
{
    public Guid OriginDeviceId { get; init; }
    public Guid OriginInstanceId { get; init; }
    public long UserKeyEpoch { get; init; }
    public long MembershipEpoch { get; init; }
    public long OriginRevision { get; init; }

    public bool IsValid =>
        OriginDeviceId != Guid.Empty &&
        OriginInstanceId != Guid.Empty &&
        UserKeyEpoch > 0 &&
        MembershipEpoch > 0 &&
        OriginRevision > 0;

    public void WriteTo(Sha256HashBuilder hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        hash.Write(OriginDeviceId);
        hash.Write(OriginInstanceId);
        hash.Write(UserKeyEpoch);
        hash.Write(MembershipEpoch);
        hash.Write(OriginRevision);
    }
}
