namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal sealed class ActiveObserverTaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _activeTasks = [];

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _activeTasks.Count;
        }
    }

    public void Start(Func<Task> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        // Track the owned placeholder before observer execution can complete synchronously.
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _activeTasks.Add(completion.Task);

        _ = RunObserverAsync(observer, completion);
        _ = completion.Task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_gate)
                    _activeTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WaitForCompletionAsync()
    {
        while (true)
        {
            Task[] active;
            lock (_gate)
            {
                if (_activeTasks.Count == 0)
                    return;
                active = [.. _activeTasks];
            }

            try
            {
                await Task.WhenAll(active);
            }
            catch
            {
            }
        }
    }

    private static async Task RunObserverAsync(
        Func<Task> observer,
        TaskCompletionSource completion)
    {
        try
        {
            await observer();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
