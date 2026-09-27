using Avalonia.Threading;

namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class AvaloniaUiDispatcher : IWindowsUiDispatcher
{
    public async Task<T> InvokeAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return callback();
        });
    }
}
