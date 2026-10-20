using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcRequestEnvelope(
    IpcOperationId OperationId,
    byte[]? Payload);
