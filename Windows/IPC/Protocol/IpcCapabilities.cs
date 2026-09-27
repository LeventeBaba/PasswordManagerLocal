namespace PasswordManagerLocal.Windows.Ipc.Protocol;

[Flags]
public enum IpcCapabilities
{
    None = 0,
    Control = 1,
    UiActivation = 2,
    Status = 4,
    EndpointRpc = 8
}
