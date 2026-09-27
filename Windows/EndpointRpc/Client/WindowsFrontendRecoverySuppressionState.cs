namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal enum WindowsFrontendRecoverySuppressionState
{
    None = 0,
    DatabaseReset = 1,
    IntentionalShutdown = 2,
    Disposed = 3
}
