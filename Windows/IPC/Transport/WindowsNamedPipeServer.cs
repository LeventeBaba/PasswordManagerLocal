using PasswordManagerLocal.Windows.Ipc.Protocol;
using System.IO.Pipes;

namespace PasswordManagerLocal.Windows.Ipc.Transport;

public sealed class WindowsNamedPipeServer : IWindowsIpcConnectionListener
{
    private const int PipeBufferSize = 64 * 1024;

    private readonly string _pipeName;
    private readonly IpcFrameCodec _frameCodec;

    public WindowsNamedPipeServer(string pipeName, IpcFrameCodec frameCodec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _frameCodec = frameCodec ?? throw new ArgumentNullException(nameof(frameCodec));
    }

    public async Task<IWindowsIpcConnection> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        var stream = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeBufferSize,
            PipeBufferSize);

        try
        {
            await stream.WaitForConnectionAsync(cancellationToken);
            var peerProcessId = new WindowsNamedPipePeerProcessIdProvider()
                .GetClientProcessId(stream.SafePipeHandle);
            return new WindowsIpcConnection(stream, _frameCodec, peerProcessId);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
