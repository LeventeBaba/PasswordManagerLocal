namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed record UserSnapshotReceiptResult(
    Guid UserId,
    Guid OriginDeviceId,
    Guid OriginInstanceId,
    long OriginRevision,
    byte[] SnapshotHash,
    UserSnapshotReceiptState State,
    string? Detail = null);
