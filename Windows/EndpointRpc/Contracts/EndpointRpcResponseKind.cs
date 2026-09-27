namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointRpcResponseKind : byte
{
    Failure = 0,
    InlineResult = 1,
    LargeResult = 2
}
