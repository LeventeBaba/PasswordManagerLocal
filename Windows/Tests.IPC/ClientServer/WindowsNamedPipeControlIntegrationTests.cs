using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
[DoNotParallelize]
public sealed class WindowsNamedPipeControlIntegrationTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task RealPipeHandshakeVerifiesProcessAndSessionAndServesPing()
    {
        EnsureWindows();
        var pipeName = UniquePipeName();
        await using var host = CreateHost(pipeName, new SingleUiConnectionCoordinator());
        await host.StartAsync();
        await using var client = await ConnectAsync(pipeName, Guid.NewGuid());
        var control = new WindowsIpcControlClient(client, new WindowsIpcSerializer());

        var response = await control.PingAsync();

        Assert.AreNotEqual(default(DateTimeOffset), response.ServerTimeUtc);
        Assert.AreEqual(TimeSpan.Zero, response.ServerTimeUtc.Offset);
        Assert.AreEqual(Environment.ProcessId, client.VerifiedServerProcessId);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task RealPipeRejectsClaimedProcessIdentityThatDoesNotMatchConnectedProcess()
    {
        EnsureWindows();
        var pipeName = UniquePipeName();
        await using var host = CreateHost(pipeName, new SingleUiConnectionCoordinator());
        await host.StartAsync();
        await using var connection = await new WindowsNamedPipeClient(pipeName, new IpcFrameCodec())
            .ConnectAsync();
        await using var client = new WindowsIpcClient(
            connection,
            new WindowsIpcSerializer(),
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control | IpcCapabilities.Status,
                Environment.ProcessId + 1,
                Process.GetCurrentProcess().SessionId,
                Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(() => client.HandshakeAsync());

        Assert.AreEqual(IpcErrorCode.RequestRejected, exception.Error.ErrorCode);
    }

    [TestMethod]
    [Timeout(25_000)]
    public async Task RealPipeRegistrationReplacementUsesNewGenerationAndIgnoresStaleDisconnect()
    {
        EnsureWindows();
        var coordinator = new SingleUiConnectionCoordinator();
        var pipeName = UniquePipeName();
        await using var host = CreateHost(pipeName, coordinator);
        await host.StartAsync();
        var instanceId = Guid.NewGuid();
        await using var first = await ConnectAsync(pipeName, instanceId);
        var firstControl = new WindowsIpcControlClient(first, new WindowsIpcSerializer());
        await firstControl.RegisterUiConnectionAsync();
        var firstRegistration = coordinator.Registration;
        Assert.IsNotNull(firstRegistration);

        await using var second = await ConnectAsync(pipeName, instanceId);
        var secondControl = new WindowsIpcControlClient(second, new WindowsIpcSerializer());
        await secondControl.RegisterUiConnectionAsync();
        var secondRegistration = coordinator.Registration;
        Assert.IsNotNull(secondRegistration);
        Assert.IsTrue(firstRegistration.Generation > 0);
        Assert.IsTrue(secondRegistration.Generation > firstRegistration.Generation);
        Assert.AreEqual(second.ServerConnectionId, secondRegistration.ConnectionId);

        await first.DisposeAsync();
        await WaitForAsync(() => host.ActiveSessionCount == 1, TimeSpan.FromSeconds(5));

        Assert.AreEqual(second.ServerConnectionId, coordinator.Registration?.ConnectionId);
        await secondControl.UnregisterUiConnectionAsync();
        Assert.IsNull(coordinator.Registration);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task HostDisposalCancelsRealPipeConnectionAndCompletesClientObserver()
    {
        EnsureWindows();
        var pipeName = UniquePipeName();
        var host = CreateHost(pipeName, new SingleUiConnectionCoordinator());
        await host.StartAsync();
        await using var client = await ConnectAsync(pipeName, Guid.NewGuid());

        await host.DisposeAsync();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(client.IsConnected);
    }

    private static WindowsIpcServerHost CreateHost(
        string pipeName,
        IUiConnectionCoordinator coordinator)
    {
        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();
        var dispatcher = new WindowsIpcRequestDispatcher(
            [
                new PingWindowsIpcRequestHandler(),
                new RegisterUiConnectionWindowsIpcRequestHandler(coordinator),
                new UnregisterUiConnectionWindowsIpcRequestHandler(coordinator)
            ],
            validator);
        var factory = new WindowsIpcServerConnectionSessionFactory(
            serializer,
            dispatcher,
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                [IpcPeerRole.Ui],
                IpcCapabilities.Control | IpcCapabilities.Status),
            uiCoordinator: coordinator,
            contractValidator: validator);
        return new WindowsIpcServerHost(
            new WindowsNamedPipeServer(pipeName, new IpcFrameCodec()),
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 4));
    }

    private static async Task<WindowsIpcClient> ConnectAsync(string pipeName, Guid instanceId)
    {
        var connection = await new WindowsNamedPipeClient(pipeName, new IpcFrameCodec())
            .ConnectAsync();
        var client = new WindowsIpcClient(
            connection,
            new WindowsIpcSerializer(),
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control | IpcCapabilities.Status,
                Environment.ProcessId,
                Process.GetCurrentProcess().SessionId,
                instanceId));
        try
        {
            await client.HandshakeAsync();
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static string UniquePipeName() =>
        $"PasswordManagerLocal.Phase9.Control.{Guid.NewGuid():N}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This named-pipe integration test requires Windows.");
    }
}
