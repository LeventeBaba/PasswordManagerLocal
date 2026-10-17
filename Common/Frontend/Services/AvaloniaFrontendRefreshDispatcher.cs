using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class AvaloniaFrontendRefreshDispatcher : IFrontendRefreshDispatcher
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
            return operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, DispatcherPriority.Background);

        return completion.Task.WaitAsync(cancellationToken);
    }
}
