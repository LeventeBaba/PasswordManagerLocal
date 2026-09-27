namespace PasswordManagerLocal.Windows.Ipc.Server;

internal sealed class WindowsIpcServerSessionLifetime
{
    private readonly IWindowsIpcServerSession _session;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private int _started;

    public WindowsIpcServerSessionLifetime(IWindowsIpcServerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task Completion => _completion.Task;

    public void Start(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The IPC server session has already started.");

        _ = RunAsync(cancellationToken);
    }

    public Task DisposeAsync()
    {
        lock (_gate)
            return _disposeTask ??= DisposeCoreAsync();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.RunAsync(cancellationToken);
        }
        catch
        {
        }
        finally
        {
            await DisposeAsync();
            _completion.TrySetResult();
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _session.DisposeAsync();
        }
        catch
        {
        }
    }
}
