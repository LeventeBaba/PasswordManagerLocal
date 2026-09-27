namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed record NetworkDeltaApplyResult(
    long AppliedTimestamp,
    UserSnapshotReceiptResult? UserSnapshotReceipt = null,
    UserControlOperationReceiptResult? UserControlOperationReceipt = null);
