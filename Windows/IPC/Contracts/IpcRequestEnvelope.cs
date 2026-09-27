using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcRequestEnvelope(
    long CorrelationId,
    IpcOperationId OperationId,
    byte[]? Payload);
