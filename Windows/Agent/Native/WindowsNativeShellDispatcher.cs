using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Native;

internal sealed class WindowsNativeShellDispatcher : IDisposable
{
    private readonly IWindowsNativeMessagePoster _messagePoster;
    private readonly uint _dispatchMessage;
    private readonly int _ownerManagedThreadId;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<PendingAction> _pending = new();
    private bool _wakePending;
    private bool _accepting = true;
    private int _disposed;

    internal WindowsNativeShellDispatcher(
        IWindowsNativeMessagePoster messagePoster,
        uint dispatchMessage,
        int ownerManagedThreadId,
        int capacity = 256)
    {
        _messagePoster = messagePoster ?? throw new ArgumentNullException(nameof(messagePoster));
        _dispatchMessage = dispatchMessage;
        _ownerManagedThreadId = ownerManagedThreadId;
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal bool TryPost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryPost(new PendingAction(action, static () => { }));
    }

    internal Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (Environment.CurrentManagedThreadId == _ownerManagedThreadId)
        {
            lock (_gate)
            {
                if (!_accepting || Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(WindowsNativeShellDispatcher));
            }
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAction = new PendingAction(
            () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    action();
                    completion.TrySetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            () => completion.TrySetException(
                new ObjectDisposedException(nameof(WindowsNativeShellDispatcher))));

        if (!TryPost(pendingAction))
            pendingAction.Reject();
        return completion.Task;
    }

    internal void DrainPendingActions()
    {
        PendingAction[] actions;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                _wakePending = false;
                return;
            }

            actions = _pending.ToArray();
            _pending.Clear();
            _wakePending = false;
        }

        foreach (var action in actions)
        {
            try { action.Execute(); }
            catch (Exception exception)
            {
                Trace.TraceError($"A dispatched native shell action failed: {exception.GetType().Name}");
            }
        }
    }

    internal void StopAccepting()
    {
        PendingAction[] rejected;
        lock (_gate)
        {
            _accepting = false;
            rejected = _pending.ToArray();
            _pending.Clear();
            _wakePending = false;
        }

        Reject(rejected);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        StopAccepting();
        GC.SuppressFinalize(this);
    }

    private bool TryPost(PendingAction action)
    {
        PendingAction[] rejected;
        lock (_gate)
        {
            if (!_accepting || Volatile.Read(ref _disposed) != 0 || _pending.Count >= _capacity)
                return false;

            _pending.Enqueue(action);
            if (_wakePending)
                return true;

            _wakePending = true;
            if (_messagePoster.TryPostMessage(_dispatchMessage))
                return true;

            rejected = _pending.ToArray();
            _pending.Clear();
            _wakePending = false;
        }

        Reject(rejected);
        return false;
    }

    private static void Reject(IEnumerable<PendingAction> actions)
    {
        foreach (var action in actions)
        {
            try { action.Reject(); }
            catch (Exception exception)
            {
                Trace.TraceError($"A rejected native shell action failed to complete: {exception.GetType().Name}");
            }
        }
    }

    private sealed class PendingAction
    {
        internal PendingAction(Action execute, Action reject)
        {
            Execute = execute ?? throw new ArgumentNullException(nameof(execute));
            Reject = reject ?? throw new ArgumentNullException(nameof(reject));
        }

        internal Action Execute { get; }
        internal Action Reject { get; }
    }
}
