using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class HostAdapterTestConnection : IWindowsIpcConnection
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected { get; private set; } = true;
    public int DisposeCount { get; private set; }

    public ValueTask<IpcFrame?> ReadFrameAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IpcFrame?>(null);

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
