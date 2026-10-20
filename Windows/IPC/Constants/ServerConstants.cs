namespace PasswordManagerLocal.Windows.Ipc.Constants;

public static class ServerConstants
{
    public const int WindowsIpcServerConnectionSessionStopKindNone = 0;
    public const int WindowsIpcServerConnectionSessionStopKindRequested = 1;
    public const int WindowsIpcServerConnectionSessionStopKindRequestFailure = 2;
    public const int WindowsIpcServerHostOptionsDefaultMaximumActiveConnections = 8;
    public const int WindowsIpcServerHostOptionsMaximumConfigurableActiveConnections = 64;
    public const int WindowsIpcServerOptionsDefaultMaximumActiveRequestsPerConnection = 64;
    public const int WindowsIpcServerOptionsMaximumConfigurableActiveRequestsPerConnection = 1024;
}
