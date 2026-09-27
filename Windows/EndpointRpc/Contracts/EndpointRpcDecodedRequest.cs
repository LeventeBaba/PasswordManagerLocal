namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed record EndpointRpcDecodedRequest(
    EndpointRpcRequestKind RequestKind,
    EndpointOperationId? OperationId,
    byte[] Payload);
