using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class HostAdapterTestConnection : IWindowsIpcConnection
{
    private readonly bool _blockReads;
    private readonly TaskCompletionSource _readStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public HostAdapterTestConnection(bool blockReads = false) =>
        _blockReads = blockReads;

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected { get; private set; } = true;
    public int DisposeCount { get; private set; }
    public Task ReadStarted => _readStarted.Task;

    public async ValueTask<IpcFrame?> ReadFrameAsync(
        CancellationToken cancellationToken = default)
    {
        _readStarted.TrySetResult();
        if (_blockReads)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }

    public ValueTask WriteFrameAsync(
        IpcFrame frame,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask<IpcFrameWriteResult> TryWriteFrameAsync(
        IpcFrame frame,
        Func<bool> tryBeginWrite,
        CancellationToken queuedCancellationToken,
        CancellationToken shutdownCancellationToken) =>
        ValueTask.FromResult(IpcFrameWriteResult.SkippedBeforeTransmission);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
