namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsUiShutdownBridge
{
    Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);
}
