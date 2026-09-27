using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed record EndpointRpcDispatchResult(
    byte[]? Result,
    EndpointLargeResultDescriptor? LargeResult,
    EndpointRpcError? Error)
{
    public bool IsSuccess => Error is null;
    public bool IsLargeResult => LargeResult is not null;

    public static EndpointRpcDispatchResult Success(byte[] result) =>
        new(result ?? throw new ArgumentNullException(nameof(result)), null, null);

    public static EndpointRpcDispatchResult Large(EndpointLargeResultDescriptor descriptor) =>
        new(null, descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null);

    public static EndpointRpcDispatchResult Failure(EndpointRpcError error) =>
        new(null, null, error ?? throw new ArgumentNullException(nameof(error)));
}
