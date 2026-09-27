using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsNamedPipeTransportTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task CurrentUserNamedPipeClientAndServerExchangeAFrame()
    {
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellationToken = timeoutCancellation.Token;
        var pipeName = $"PasswordManagerLocal.Common.Tests.{Guid.NewGuid():N}";
        var codec = new IpcFrameCodec();
        var server = new WindowsNamedPipeServer(pipeName, codec);
        var clientFactory = new WindowsNamedPipeClient(pipeName, codec);
        var acceptTask = server.AcceptAsync(cancellationToken);
        var connectTask = clientFactory.ConnectAsync(cancellationToken);

        await Task.WhenAll(acceptTask, connectTask);
        await using var client = await connectTask;
        await using var serverConnection = await acceptTask;
        var payload = new byte[] { 7, 8, 9 };
        var frame = new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Request,
                IpcFrameFlags.None,
                3,
                payload.Length),
            payload);

        var readTask = serverConnection.ReadFrameAsync(cancellationToken).AsTask();
        await client.WriteFrameAsync(frame, cancellationToken);
        var received = await readTask;

        Assert.IsNotNull(received);
        Assert.AreEqual(frame.Header, received.Header);
        CollectionAssert.AreEqual(payload, received.Payload);
    }
}
