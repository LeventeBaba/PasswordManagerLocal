using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Utils;

public sealed class SyncVersionStampComparer : IComparer<SyncVersionStamp>, IEqualityComparer<SyncVersionStamp>
{
    public static SyncVersionStampComparer Instance { get; } = new();

    private SyncVersionStampComparer() { }

    public int Compare(SyncVersionStamp? x, SyncVersionStamp? y)
    {
        Validate(x, nameof(x));
        Validate(y, nameof(y));

        var result = x!.PhysicalTimeUnixMilliseconds.CompareTo(y!.PhysicalTimeUnixMilliseconds);
        if (result != 0)
            return result;

        result = x.LogicalCounter.CompareTo(y.LogicalCounter);
        if (result != 0)
            return result;

        result = CompareGuidCanonical(x.OriginDeviceId, y.OriginDeviceId);
        return result != 0 ? result : CompareGuidCanonical(x.OriginInstanceId, y.OriginInstanceId);
    }

    public bool Equals(SyncVersionStamp? x, SyncVersionStamp? y)
    {
        Validate(x, nameof(x));
        Validate(y, nameof(y));
        return Compare(x, y) == 0;
    }

    public int GetHashCode(SyncVersionStamp obj)
    {
        Validate(obj, nameof(obj));
        return HashCode.Combine(
            obj.PhysicalTimeUnixMilliseconds,
            obj.LogicalCounter,
            obj.OriginDeviceId,
            obj.OriginInstanceId);
    }

    public static void Validate(SyncVersionStamp? stamp, string? parameterName = null)
    {
        if (stamp is null || !stamp.IsValid)
            throw new InvalidDataException($"Invalid or uninitialized synchronization version stamp{FormatParameter(parameterName)}.");
    }

    public static SyncVersionStamp Max(SyncVersionStamp first, SyncVersionStamp second) =>
        Instance.Compare(first, second) >= 0 ? first : second;

    private static int CompareGuidCanonical(Guid left, Guid right) =>
        string.CompareOrdinal(left.ToString("N"), right.ToString("N"));

    private static string FormatParameter(string? parameterName) =>
        string.IsNullOrWhiteSpace(parameterName) ? string.Empty : $" ({parameterName})";
}
