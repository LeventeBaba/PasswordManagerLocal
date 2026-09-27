namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsUiDispatcher
{
    Task<T> InvokeAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken = default);
}
