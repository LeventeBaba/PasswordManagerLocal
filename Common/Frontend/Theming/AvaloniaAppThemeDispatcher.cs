using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Theming;

public sealed class AvaloniaAppThemeDispatcher : IAppThemeDispatcher
{
    public async Task InvokeAsync(
        Action operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            operation();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0;
        using var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                completion.TrySetCanceled(cancellationToken);
        });
        Dispatcher.UIThread.Post(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return;
            try
            {
                operation();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, DispatcherPriority.Normal);
        await completion.Task.ConfigureAwait(false);
    }
}
