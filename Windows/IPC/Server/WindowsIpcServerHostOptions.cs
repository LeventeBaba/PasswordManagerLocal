namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerHostOptions
{

    public WindowsIpcServerHostOptions(
        int maximumActiveConnections = ServerConstants.WindowsIpcServerHostOptionsDefaultMaximumActiveConnections)
    {
        if (maximumActiveConnections <= 0 ||
            maximumActiveConnections > ServerConstants.WindowsIpcServerHostOptionsMaximumConfigurableActiveConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveConnections));
        }

        MaximumActiveConnections = maximumActiveConnections;
    }

    public int MaximumActiveConnections { get; }
}
