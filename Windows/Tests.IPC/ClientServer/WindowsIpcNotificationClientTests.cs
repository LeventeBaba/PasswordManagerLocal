using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Text;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcNotificationClientTests
{
    [TestMethod]
    public async Task UnsolicitedNotificationDoesNotConsumeOrCompleteCorrelatedResponse()
    {
        await using var session = await TestSession.CreateAsync();
        var registration = await session.Client.SendAsync(IpcOperationId.RegisterUiConnection);
        Assert.IsTrue(registration.IsSuccess);
        Assert.AreEqual(1, session.Registry.RegisteredSessionCount);
        var enabled = await session.ControlClient.SetChangeNotificationUserAsync(null, enabled: true);
        Assert.IsTrue(enabled.Accepted);

        var notification = new IpcChangeNotificationDto(
            Guid.NewGuid(),
            1,
            FrontendChangeScope.Passwords | FrontendChangeScope.UserProfile,
            DateTimeOffset.UtcNow,
            RequiresAuthoritativeRefresh: false);
        Assert.IsTrue(session.Registry.TryEnqueue(session.Client.ServerConnectionId!.Value, notification));
        var responseTask = session.Client.SendAsync(IpcOperationId.Ping);
        var response = await responseTask;

        Assert.IsTrue(response.IsSuccess);
        await using var notifications = session.Client.ReadChangeNotificationsAsync().GetAsyncEnumerator();
        Assert.IsTrue(await notifications.MoveNextAsync());
        Assert.AreEqual(notification.RuntimeInstanceId, notifications.Current.RuntimeInstanceId);
        Assert.AreEqual(notification.Sequence, notifications.Current.Sequence);
        Assert.AreEqual(notification.Scope, notifications.Current.Scope);
        Assert.IsFalse(notifications.Current.RequiresAuthoritativeRefresh);
    }

    [TestMethod]
    public async Task MalformedNotificationClosesClientWithPayloadSafeFailureAndNoCallback()
    {
        await using var session = await TestSession.CreateAsync();
        const string privateMarker = "private-notification-payload-marker";
        var payload = Encoding.UTF8.GetBytes($"{{\"unknown\":\"{privateMarker}\"}}");
        await session.Pair.Server.WriteFrameAsync(new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Notification,
                IpcFrameFlags.None,
                0,
                payload.Length),
            payload));

        await session.Client.Completion;

        Assert.IsNotNull(session.Client.TerminationFailure);
        Assert.IsFalse(session.Client.TerminationFailure!.ToString().Contains(privateMarker, StringComparison.Ordinal));
        await using var notifications = session.Client.ReadChangeNotificationsAsync().GetAsyncEnumerator();
        Assert.IsFalse(await notifications.MoveNextAsync());
    }

    [TestMethod]
    public async Task FailedNotificationWriteClosesConnectionWithoutExposingPayload()
    {
        await using var session = await TestSession.CreateAsync();
        var registration = await session.Client.SendAsync(IpcOperationId.RegisterUiConnection);
        Assert.IsTrue(registration.IsSuccess);
        var enabled = await session.ControlClient.SetChangeNotificationUserAsync(null, enabled: true);
        Assert.IsTrue(enabled.Accepted);
        session.Pair.Server.FailNotificationWrites = true;
        var runtimeId = Guid.NewGuid();
        Assert.IsTrue(session.Registry.TryEnqueue(
            session.Client.ServerConnectionId!.Value,
            new IpcChangeNotificationDto(
                runtimeId,
                1,
                FrontendChangeScope.Everything,
                DateTimeOffset.UtcNow,
                RequiresAuthoritativeRefresh: true)));

        await session.Client.Completion;
        await session.ServerTask;

        Assert.IsNotNull(session.Server.TerminationFailure);
        Assert.IsFalse(session.Server.TerminationFailure!.ToString().Contains(
            runtimeId.ToString(),
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ClientCannotSendUnsolicitedNotificationFramesToServer()
    {
        await using var session = await TestSession.CreateAsync();
        var serializer = new WindowsIpcSerializer();
        var runtimeId = Guid.NewGuid();
        var payload = serializer.Serialize(
            new IpcChangeNotificationDto(
                runtimeId,
                1,
                FrontendChangeScope.Passwords,
                DateTimeOffset.UtcNow,
                RequiresAuthoritativeRefresh: false),
            WindowsIpcJsonContext.Default.IpcChangeNotificationDto);
        await session.Pair.Client.WriteFrameAsync(new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Notification,
                IpcFrameFlags.None,
                0,
                payload.Length),
            payload));

        await session.ServerTask;

        Assert.IsNotNull(session.Server.TerminationFailure);
        Assert.IsFalse(session.Server.TerminationFailure!.ToString().Contains(
            runtimeId.ToString(),
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NotificationStreamCancellationAndClientDisposalStopIteration()
    {
        await using var session = await TestSession.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await using var notifications = session.Client
            .ReadChangeNotificationsAsync(cancellation.Token)
            .GetAsyncEnumerator();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await notifications.MoveNextAsync();
        });

        await session.Client.DisposeAsync();
        await using var afterDispose = session.Client.ReadChangeNotificationsAsync().GetAsyncEnumerator();
        Assert.IsFalse(await afterDispose.MoveNextAsync());
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private TestSession(
            InMemoryIpcConnectionPair pair,
            WindowsIpcClient client,
            WindowsIpcServerConnectionSession server,
            WindowsIpcNotificationSessionRegistry registry,
            WindowsIpcControlClient controlClient,
            Task serverTask)
        {
            Pair = pair;
            Client = client;
            Server = server;
            Registry = registry;
            ControlClient = controlClient;
            ServerTask = serverTask;
        }

        public InMemoryIpcConnectionPair Pair { get; }
        public WindowsIpcClient Client { get; }
        public WindowsIpcServerConnectionSession Server { get; }
        public WindowsIpcNotificationSessionRegistry Registry { get; }
        public WindowsIpcControlClient ControlClient { get; }
        public Task ServerTask { get; }

        public static async Task<TestSession> CreateAsync()
        {
            var pair = new InMemoryIpcConnectionPair();
            var serializer = new WindowsIpcSerializer();
            var validator = new WindowsIpcContractValidator();
            var coordinator = new SingleUiConnectionCoordinator();
            var registry = new WindowsIpcNotificationSessionRegistry();
            var dispatcher = new WindowsIpcRequestDispatcher(
                new IWindowsIpcRequestHandler[]
                {
                    new PingWindowsIpcRequestHandler(),
                    new RegisterUiConnectionWindowsIpcRequestHandler(coordinator),
                    new SetChangeNotificationUserWindowsIpcRequestHandler(
                        new RegistryNotificationSelector(registry))
                },
                validator,
                new WindowsIpcOperationAuthorizer(coordinator));
            var capabilities = IpcCapabilities.Control | IpcCapabilities.ChangeNotifications;
            var server = new WindowsIpcServerConnectionSession(
                pair.Server,
                serializer,
                dispatcher,
                new WindowsIpcServerOptions(
                    IpcPeerRole.Agent,
                    new[] { IpcPeerRole.Ui },
                    capabilities),
                uiConnectionCoordinator: coordinator,
                contractValidator: validator,
                notificationSessionRegistry: registry);
            var serverTask = server.RunAsync();
            var client = new WindowsIpcClient(
                pair.Client,
                serializer,
                new WindowsIpcClientOptions(
                    IpcPeerRole.Ui,
                    IpcPeerRole.Agent,
                    capabilities,
                    Environment.ProcessId,
                    0,
                    Guid.NewGuid(),
                    requiredServerCapabilities: IpcCapabilities.ChangeNotifications),
                validator);
            await client.HandshakeAsync();
            return new TestSession(
                pair,
                client,
                server,
                registry,
                new WindowsIpcControlClient(client, serializer, validator),
                serverTask);
        }

        private sealed class RegistryNotificationSelector(
            WindowsIpcNotificationSessionRegistry registry) : IWindowsIpcChangeNotificationUserSelector
        {
            public Task<bool> SetUserFilterAsync(
                Guid connectionId,
                Guid? userId,
                bool enabled,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (userId == Guid.Empty || !registry.HasTarget(connectionId))
                    return Task.FromResult(false);
                return Task.FromResult(enabled
                    ? registry.Enable(connectionId)
                    : Disable(connectionId));
            }

            private bool Disable(Guid connectionId)
            {
                registry.Disable(connectionId);
                return true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); } catch { }
            try { await ServerTask; } catch { }
            try { await Server.DisposeAsync(); } catch { }
        }
    }
}
