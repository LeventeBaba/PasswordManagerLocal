using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed record EndpointRpcTransportResponse(
    EndpointRpcResponseKind ResponseKind,
    byte[]? InlinePayload,
    EndpointLargeResultDescriptor? LargeResult,
    EndpointRpcError? Error)
{
    public static EndpointRpcTransportResponse Inline(byte[] payload) =>
        new(
            EndpointRpcResponseKind.InlineResult,
            payload ?? throw new ArgumentNullException(nameof(payload)),
            null,
            null);

    public static EndpointRpcTransportResponse Large(EndpointLargeResultDescriptor descriptor) =>
        new(
            EndpointRpcResponseKind.LargeResult,
            null,
            descriptor ?? throw new ArgumentNullException(nameof(descriptor)),
            null);

    public static EndpointRpcTransportResponse Failure(EndpointRpcError error) =>
        new(
            EndpointRpcResponseKind.Failure,
            null,
            null,
            error ?? throw new ArgumentNullException(nameof(error)));
}
