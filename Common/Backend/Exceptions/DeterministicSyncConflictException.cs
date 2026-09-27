using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Exceptions;

public sealed class DeterministicSyncConflictException : InvalidOperationException
{
    public DeterministicSyncConflictException(
        string itemType,
        Guid itemId,
        SyncVersionStamp version,
        ReadOnlySpan<byte> firstContentHash,
        ReadOnlySpan<byte> secondContentHash,
        Guid? userId = null)
        : base(BuildMessage(itemType, itemId, version, firstContentHash, secondContentHash, userId))
    {
        ItemType = itemType;
        ItemId = itemId;
        Version = version;
        UserId = userId;
        FirstContentHashHex = Convert.ToHexString(firstContentHash);
        SecondContentHashHex = Convert.ToHexString(secondContentHash);
    }

    public Guid? UserId { get; }
    public string ItemType { get; }
    public Guid ItemId { get; }
    public SyncVersionStamp Version { get; }
    public string FirstContentHashHex { get; }
    public string SecondContentHashHex { get; }

    public DeterministicSyncConflictException WithUserId(Guid userId) =>
        new(
            ItemType,
            ItemId,
            Version,
            Convert.FromHexString(FirstContentHashHex),
            Convert.FromHexString(SecondContentHashHex),
            userId);

    private static string BuildMessage(
        string itemType,
        Guid itemId,
        SyncVersionStamp version,
        ReadOnlySpan<byte> firstContentHash,
        ReadOnlySpan<byte> secondContentHash,
        Guid? userId)
    {
        var user = userId.HasValue ? $" user {userId.Value:N}," : string.Empty;
        return $"Deterministic synchronization conflict for{user} {itemType} {itemId:N} at " +
               $"{version.PhysicalTimeUnixMilliseconds}:{version.LogicalCounter}:" +
               $"{version.OriginDeviceId:N}:{version.OriginInstanceId:N}; " +
               $"content hashes {Convert.ToHexString(firstContentHash)} and {Convert.ToHexString(secondContentHash)}.";
    }
}
