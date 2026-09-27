namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed record UserControlOperationReceiptResult(
    Guid OperationId,
    Guid UserId,
    Guid OriginDeviceId,
    Guid OriginInstanceId,
    long OriginSequence,
    byte[] OperationHash,
    UserControlOperationReceiptState State,
    string? Detail = null);
