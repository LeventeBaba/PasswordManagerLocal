namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcResponseEnvelope(
    IpcResponsePayloadKind PayloadKind,
    byte[]? Result,
    IpcError? Error)
{
    public bool IsSuccess => PayloadKind == IpcResponsePayloadKind.Success;

    public static IpcResponseEnvelope Success(byte[]? result = null) =>
        new(IpcResponsePayloadKind.Success, result, null);

    public static IpcResponseEnvelope Failure(IpcError error) =>
        new(IpcResponsePayloadKind.Failure, null, error ?? throw new ArgumentNullException(nameof(error)));

    public static IpcResponseEnvelope EndpointFailure(byte[] errorPayload) =>
        new(
            IpcResponsePayloadKind.EndpointFailure,
            errorPayload ?? throw new ArgumentNullException(nameof(errorPayload)),
            null);

    public static IpcResponseEnvelope LargeResult(byte[] descriptorPayload) =>
        new(
            IpcResponsePayloadKind.LargeResult,
            descriptorPayload ?? throw new ArgumentNullException(nameof(descriptorPayload)),
            null);
}
