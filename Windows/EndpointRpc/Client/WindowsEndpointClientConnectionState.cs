namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public enum WindowsEndpointClientConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Ready = 2,
    Reconnecting = 3,
    Unavailable = 4,
    IntentionalShutdown = 5,
    Disposed = 6
}
