namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerHostOptions
{
    public const int DefaultMaximumActiveConnections = 8;
    public const int MaximumConfigurableActiveConnections = 64;

    public WindowsIpcServerHostOptions(
        int maximumActiveConnections = DefaultMaximumActiveConnections)
    {
        if (maximumActiveConnections <= 0 ||
            maximumActiveConnections > MaximumConfigurableActiveConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveConnections));
        }

        MaximumActiveConnections = maximumActiveConnections;
    }

    public int MaximumActiveConnections { get; }
}
