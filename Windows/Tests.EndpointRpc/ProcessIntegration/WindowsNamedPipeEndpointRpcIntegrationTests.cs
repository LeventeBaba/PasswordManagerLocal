using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.ProcessIntegration;

[TestClass]
[DoNotParallelize]
public sealed class WindowsNamedPipeEndpointRpcIntegrationTests
{
    [TestMethod]
    [Timeout(25_000)]
    public async Task RealEndpointPipeCorrelatesOutOfOrderConcurrentResponses()
    {
        EnsureWindows();
        var identity = CreateIdentity();
        var resolver = CreateResolver(identity);
        var admission = new ControllableEndpointRpcAdmissionPolicy();
        var endpoints = new OutOfOrderEndpointTestEndpoints();
        var pipeName = UniquePipeName();
        await using var factory = new EndpointRpcServerSessionFactory(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            new EndpointRpcConnectionAuthorizer(resolver, admission),
            admission);
        await using var host = new WindowsIpcServerHost(
            new WindowsNamedPipeServer(pipeName, new IpcFrameCodec()),
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 4));
        await host.StartAsync();
        await using var transport = await new WindowsNamedPipeEndpointRpcConnector(pipeName, identity)
            .ConnectAsync();
        await using var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        var blocked = proxy.LogoutAsync(EndpointRpcTestData.Token);
        PasswordManagerLocal.Common.Contracts.Responses.LocalDeviceInfoResponse device;
        try
        {
            await endpoints.LogoutStarted.WaitAsync(TimeSpan.FromSeconds(5));
            device = await proxy.GetLocalDeviceInfoAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            endpoints.ReleaseLogout();
        }
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(EndpointRpcTestData.ItemId, device.DeviceId);
        Assert.AreEqual("phase9-real-pipe", device.TlsCertFingerprint);
    }

    [TestMethod]
    [Timeout(25_000)]
    public async Task RealEndpointPipeRejectsConcurrentReplacementThenAllowsItAfterDisconnect()
    {
        EnsureWindows();
        var identity = CreateIdentity();
        var resolver = CreateResolver(identity);
        var admission = new ControllableEndpointRpcAdmissionPolicy();
        var pipeName = UniquePipeName();
        await using var factory = new EndpointRpcServerSessionFactory(
            new FixedEndpointRpcEndpointAdapter(new SuccessfulRecordingEndpoints()),
            new EndpointRpcConnectionAuthorizer(resolver, admission),
            admission);
        await using var host = new WindowsIpcServerHost(
            new WindowsNamedPipeServer(pipeName, new IpcFrameCodec()),
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 4));
        await host.StartAsync();
        var connector = new WindowsNamedPipeEndpointRpcConnector(pipeName, identity);
        await using var first = await connector.ConnectAsync();

        var conflict = await Assert.ThrowsAsync<EndpointRpcRemoteException>(async () =>
        {
            await using var ignored = await connector.ConnectAsync();
        });
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, conflict.Error.ErrorCode);

        await first.DisposeAsync();
        await WaitForAsync(() => host.ActiveSessionCount == 0, TimeSpan.FromSeconds(5));
        await using var replacement = await connector.ConnectAsync();

        Assert.IsTrue(replacement.IsConnected);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task RealEndpointPipeRejectsRequestsAfterRegistrationGenerationBecomesStale()
    {
        EnsureWindows();
        var identity = CreateIdentity();
        var resolver = CreateResolver(identity);
        var admission = new ControllableEndpointRpcAdmissionPolicy();
        var pipeName = UniquePipeName();
        await using var factory = new EndpointRpcServerSessionFactory(
            new FixedEndpointRpcEndpointAdapter(new SuccessfulRecordingEndpoints()),
            new EndpointRpcConnectionAuthorizer(resolver, admission),
            admission);
        await using var host = new WindowsIpcServerHost(
            new WindowsNamedPipeServer(pipeName, new IpcFrameCodec()),
            factory);
        await host.StartAsync();
        await using var transport = await new WindowsNamedPipeEndpointRpcConnector(pipeName, identity)
            .ConnectAsync();
        await using var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());
        resolver.RegistrationGeneration++;

        var exception = await Assert.ThrowsAsync<EndpointRpcRemoteException>(() =>
            proxy.GetLocalDeviceInfoAsync());

        Assert.AreEqual(EndpointRpcErrorCode.AuthorizationFailed, exception.Error.ErrorCode);
    }

    private static PasswordManagerLocal.Windows.Ipc.Contracts.WindowsUiIpcIdentity CreateIdentity() =>
        new(
            Environment.ProcessId,
            Process.GetCurrentProcess().SessionId,
            Guid.NewGuid());

    private static FakeEndpointUiRegistrationResolver CreateResolver(
        PasswordManagerLocal.Windows.Ipc.Contracts.WindowsUiIpcIdentity identity) =>
        new()
        {
            ExpectedProcessId = identity.ProcessId,
            ExpectedWindowsSessionId = identity.WindowsSessionId,
            ExpectedInstanceId = identity.InstanceId,
            RegistrationGeneration = 1
        };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static string UniquePipeName() =>
        $"PasswordManagerLocal.Phase9.Endpoint.{Guid.NewGuid():N}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This endpoint named-pipe integration test requires Windows.");
    }
}
