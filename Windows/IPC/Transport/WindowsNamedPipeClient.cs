using PasswordManagerLocal.Windows.Ipc.Protocol;
using System.IO.Pipes;

namespace PasswordManagerLocal.Windows.Ipc.Transport;

public sealed class WindowsNamedPipeClient
{
    private readonly string _pipeName;
    private readonly IpcFrameCodec _frameCodec;

    public WindowsNamedPipeClient(string pipeName, IpcFrameCodec frameCodec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _frameCodec = frameCodec ?? throw new ArgumentNullException(nameof(frameCodec));
    }

    public async Task<IWindowsIpcConnection> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var stream = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await stream.ConnectAsync(cancellationToken);
            var peerProcessId = new WindowsNamedPipePeerProcessIdProvider()
                .GetServerProcessId(stream.SafePipeHandle);
            return new WindowsIpcConnection(stream, _frameCodec, peerProcessId);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
