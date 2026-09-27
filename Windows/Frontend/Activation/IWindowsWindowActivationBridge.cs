namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsWindowActivationBridge
{
    Task<bool> ActivateAsync(CancellationToken cancellationToken = default);
}
