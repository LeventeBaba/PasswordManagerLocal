using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DisposableTestWindowsIpcConnection : IWindowsIpcConnection
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => DisposeCount == 0;
    public int DisposeCount { get; private set; }

    public ValueTask<IpcFrame?> ReadFrameAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask WriteFrameAsync(IpcFrame frame, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<IpcFrameWriteResult> TryWriteFrameAsync(
        IpcFrame frame,
        Func<bool> tryBeginWrite,
        CancellationToken queuedCancellationToken,
        CancellationToken shutdownCancellationToken) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
