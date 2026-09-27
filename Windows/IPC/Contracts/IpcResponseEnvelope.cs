namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcResponseEnvelope(
    long CorrelationId,
    bool IsSuccess,
    byte[]? Result,
    IpcError? Error)
{
    public static IpcResponseEnvelope Success(long correlationId, byte[]? result = null) =>
        new(correlationId, true, result, null);

    public static IpcResponseEnvelope Failure(long correlationId, IpcError error) =>
        new(correlationId, false, null, error);
}
