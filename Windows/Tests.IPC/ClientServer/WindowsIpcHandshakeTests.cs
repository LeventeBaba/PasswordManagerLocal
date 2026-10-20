using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcHandshakeTests
{
    [TestMethod]
    public async Task CompatibleClientAndServerHandshakeSucceeds()
    {
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() });

        Assert.IsTrue(session.Client.IsHandshakeComplete);
        Assert.IsNotNull(session.Client.ServerConnectionId);
        Assert.AreNotEqual(Guid.Empty, session.Client.ServerConnectionId.Value);
    }

    [TestMethod]
    public void HandshakeDtosDoNotDuplicateTheFrameProtocolVersion()
    {
        var serializer = new WindowsIpcSerializer();
        var requestJson = System.Text.Encoding.UTF8.GetString(serializer.Serialize(
            CreateHandshakeRequest(IpcPeerRole.TestClient),
            WindowsIpcJsonContext.Default.IpcHandshakeRequest));
        var responseJson = System.Text.Encoding.UTF8.GetString(serializer.Serialize(
            new IpcHandshakeResponse(true, IpcPeerRole.Agent, Guid.NewGuid(), IpcCapabilities.Control, null),
            WindowsIpcJsonContext.Default.IpcHandshakeResponse));
        Assert.IsFalse(requestJson.Contains("protocolVersion", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains("protocolVersion", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task UnexpectedClientRoleIsRejected()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var server = CreateServer(
            pair,
            serializer,
            acceptedClientRoles: new[] { IpcPeerRole.Ui });
        var serverTask = server.RunAsync();

        await SendHandshakeAsync(
            pair,
            serializer,
            CreateHandshakeRequest(IpcPeerRole.TestClient),
            1);
        var response = await ReadHandshakeResponseAsync(pair, serializer);

        Assert.IsFalse(response.Accepted);
        Assert.AreEqual(IpcErrorCode.UnexpectedPeerRole, response.Error?.ErrorCode);
        await serverTask;
    }



    [TestMethod]
    public async Task CapabilityNotSupportedByServerIsRejectedWithStructuredError()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var serverTask = CreateServer(pair, serializer).RunAsync();

        await SendHandshakeAsync(
            pair,
            serializer,
            new IpcHandshakeRequest(
                IpcPeerRole.TestClient,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                IpcCapabilities.EndpointRpc),
            1);
        var response = await ReadHandshakeResponseAsync(pair, serializer);

        Assert.IsFalse(response.Accepted);
        Assert.AreEqual(IpcErrorCode.UnsupportedCapability, response.Error?.ErrorCode);
        Assert.IsNull(await pair.Client.ReadFrameAsync());
        await serverTask;
    }

    [TestMethod]
    public async Task MissingRequiredClientCapabilityIsRejectedWithStructuredError()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var serverTask = CreateServer(
            pair,
            serializer,
            requiredClientCapabilities: IpcCapabilities.EndpointRpc).RunAsync();

        await SendHandshakeAsync(
            pair,
            serializer,
            CreateHandshakeRequest(IpcPeerRole.TestClient),
            1);
        var response = await ReadHandshakeResponseAsync(pair, serializer);

        Assert.IsFalse(response.Accepted);
        Assert.AreEqual(IpcErrorCode.UnsupportedCapability, response.Error?.ErrorCode);
        Assert.IsNull(await pair.Client.ReadFrameAsync());
        await serverTask;
    }

    [TestMethod]
    public async Task OrdinaryRequestBeforeHandshakeIsRejected()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var serverTask = CreateServer(pair, serializer).RunAsync();
        var payload = new IpcFramePayloadCodec(serializer).EncodeRequest(
            new IpcRequestEnvelope(IpcOperationId.Ping, null),
            4);

        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 4, payload));
        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = new IpcFramePayloadCodec(serializer).DecodeResponse(
            responseFrame.Header.CorrelationId,
            responseFrame.Payload);

        Assert.IsFalse(response.IsSuccess);
        Assert.AreEqual(IpcErrorCode.HandshakeRequired, response.Error?.ErrorCode);
        Assert.IsNull(await pair.Client.ReadFrameAsync());
        await serverTask;
    }

    [TestMethod]
    public async Task DuplicateHandshakeIsRejectedAndConnectionCloses()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var serverTask = CreateServer(pair, serializer).RunAsync();
        var request = CreateHandshakeRequest(IpcPeerRole.TestClient);

        await SendHandshakeAsync(pair, serializer, request, 1);
        var accepted = await ReadHandshakeResponseAsync(pair, serializer);
        Assert.IsTrue(accepted.Accepted);

        await SendHandshakeAsync(pair, serializer, request, 2);
        var rejected = await ReadHandshakeResponseAsync(pair, serializer);

        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual(IpcErrorCode.DuplicateHandshake, rejected.Error?.ErrorCode);
        Assert.IsNull(await pair.Client.ReadFrameAsync());
        await serverTask;
    }

    private static WindowsIpcServerConnectionSession CreateServer(
        InMemoryIpcConnectionPair pair,
        WindowsIpcSerializer serializer,
        IEnumerable<IpcPeerRole>? acceptedClientRoles = null,
        IpcCapabilities requiredClientCapabilities = IpcCapabilities.None)
    {
        var roles = (acceptedClientRoles ?? new[] { IpcPeerRole.TestClient }).ToArray();
        return new WindowsIpcServerConnectionSession(
            pair.Server,
            serializer,
            new WindowsIpcRequestDispatcher(
                new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() }),
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                roles,
                IpcCapabilities.Control,
                requiredClientCapabilities: requiredClientCapabilities),
            uiConnectionCoordinator: roles.Contains(IpcPeerRole.Ui)
                ? new SingleUiConnectionCoordinator()
                : null);
    }

    private static IpcHandshakeRequest CreateHandshakeRequest(IpcPeerRole role) =>
        new(
            role,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control);

    private static async Task SendHandshakeAsync(
        InMemoryIpcConnectionPair pair,
        WindowsIpcSerializer serializer,
        IpcHandshakeRequest request,
        long correlationId)
    {
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcHandshakeRequest);
        await pair.Client.WriteFrameAsync(
            CreateFrame(IpcMessageKind.HandshakeRequest, correlationId, payload));
    }

    private static async Task<IpcHandshakeResponse> ReadHandshakeResponseAsync(
        InMemoryIpcConnectionPair pair,
        WindowsIpcSerializer serializer)
    {
        var frame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(frame);
        Assert.AreEqual(IpcMessageKind.HandshakeResponse, frame.Header.MessageKind);
        return serializer.Deserialize(
            frame.Payload,
            WindowsIpcJsonContext.Default.IpcHandshakeResponse);
    }

    private static IpcFrame CreateFrame(
        IpcMessageKind kind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                kind,
                correlationId,
                payload.Length),
            payload);
}
