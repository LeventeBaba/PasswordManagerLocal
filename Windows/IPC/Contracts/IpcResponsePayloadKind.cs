namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum IpcResponsePayloadKind : byte
{
    Success = 0,
    Failure = 1,
    EndpointFailure = 2,
    LargeResult = 3
}
