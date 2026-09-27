namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsUiActivationServerLifetime : IAsyncDisposable
{
    void RequestStop();
}
