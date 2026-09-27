namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class SyncVersionClockState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public long LastPhysicalTimeUnixMilliseconds { get; set; }
    public long LastLogicalCounter { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
