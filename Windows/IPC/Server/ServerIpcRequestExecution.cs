namespace PasswordManagerLocal.Windows.Ipc.Server;

internal sealed class ServerIpcRequestExecution : IDisposable
{
    private readonly Action _releaseCapacity;
    private int _disposeStarted;

    public ServerIpcRequestExecution(
        CancellationTokenSource cancellationSource,
        Action releaseCapacity)
    {
        CancellationSource = cancellationSource
            ?? throw new ArgumentNullException(nameof(cancellationSource));
        _releaseCapacity = releaseCapacity
            ?? throw new ArgumentNullException(nameof(releaseCapacity));
    }

    public CancellationTokenSource CancellationSource { get; }
    public Task? Task { get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        CancellationSource.Dispose();
        _releaseCapacity();
    }
}
