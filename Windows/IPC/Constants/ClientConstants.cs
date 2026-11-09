namespace PasswordManagerLocal.Windows.Ipc.Constants;

public static class ClientConstants
{
    public const long WindowsIpcClientHandshakeCorrelationId = 1;
    public const int WindowsIpcClientOptionsDefaultMaximumPendingRequests = 64;
    public const int WindowsIpcClientOptionsMaximumConfigurablePendingRequests = 1024;
}
