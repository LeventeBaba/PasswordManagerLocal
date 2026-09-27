namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointRpcRequestKind : byte
{
    PublicOperation = 1,
    GetLargeResultChunk = 2,
    ReleaseLargeResult = 3
}
