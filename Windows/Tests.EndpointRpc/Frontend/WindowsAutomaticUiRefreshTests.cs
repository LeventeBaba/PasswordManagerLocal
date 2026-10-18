using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using UiDispatcher = global::Avalonia.Threading.Dispatcher;
using Avalonia.VisualTree;
using ReactiveUI.Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Contracts.Devices;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Views;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Frontend;

/// <summary>
/// Runs the production Windows runtime client, UI-session wiring, forwarder, coordinator,
/// page view models, dispatcher and XAML bindings. Only the Agent/endpoint transports are
/// controlled. No input, F5, refresh command, or direct automatic-refresh call is injected.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WindowsAutomaticUiRefreshTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ControlledPasswordResponsesRoundTripThroughProductionRpcContract()
    {
        var state = new EndpointState(new UiTestProgress(line => TestContext.WriteLine(line)));
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        foreach (var name in new[] { "Initial password", "Password from incoming sync", "Changed while disconnected" })
        {
            state.PasswordName = name;
            var payload = await state.RespondAsync(EndpointOperationId.GetSavedPasswords, [], CancellationToken.None);
            var response = serializer.Deserialize(payload, EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse);
            validator.ValidateResponse(EndpointOperationId.GetSavedPasswords, response);
            Assert.AreEqual(name, response.Passwords.Passwords.Single().Name);
        }
    }

    [TestMethod]
    public Task PasswordAndProfileChangesReachVisibleControlsWithoutUserInput() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            fixture.Progress.Record("Checking initial password control.");
            Assert.AreEqual("Initial password", fixture.Session.ViewModel!.PasswordsViewModel.Passwords.SingleOrDefault()?.Name,
                "The controlled RPC response must reach the view model before checking its XAML projection.");
            Assert.IsTrue(fixture.HasVisibleText("Initial password"));
            fixture.State.PasswordName = "Password from incoming sync";
            await fixture.DeliverAsync(FrontendChangeScope.Passwords, token);
            Assert.AreEqual("Password from incoming sync", fixture.Session.ViewModel!.PasswordsViewModel.Passwords.SingleOrDefault()?.Name);
            Assert.IsTrue(fixture.HasVisibleText("Password from incoming sync"),
                "The password list's actual TextBlock must update without F5 or pointer input.");
            Assert.IsFalse(fixture.HasVisibleText("Initial password"));

            ((System.Windows.Input.ICommand)fixture.Session.ViewModel!.ShowProfileCommand).Execute(null);
            fixture.State.FirstName = "SyncedFirstName";
            await fixture.DeliverAsync(FrontendChangeScope.UserProfile, token);
            Assert.IsTrue(fixture.HasVisibleText("SyncedFirstName"),
                "Profile XAML must display the new authoritative first name.");
            Assert.AreEqual(0, fixture.State.ForcedPresenceReads);
        });

    [TestMethod]
    public Task DeviceRenameAndOnlineOfflineTransitionsReachVisibleControlsWithoutProbing() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            fixture.Progress.Record("Navigating to devices.");
            var devicesLoaded = fixture.WaitForDevicesLoadedAsync(token);
            ((System.Windows.Input.ICommand)fixture.Session.ViewModel!.ShowDevicesCommand).Execute(null);
            await devicesLoaded;
            await fixture.FlushBindingsAsync(token);
            Assert.IsTrue(fixture.HasVisibleText("Initial remote device"));

            fixture.State.DeviceName = "Device renamed by sync";
            fixture.State.IsOnline = true;
            await fixture.DeliverAsync(FrontendChangeScope.Devices | FrontendChangeScope.DevicePresence, token);
            var device = fixture.Session.ViewModel.ProfileViewModel.Devices.Single();
            Assert.IsTrue(device.ShowOnlineStatus);
            Assert.IsTrue(fixture.HasVisibleText("Device renamed by sync"));
            Assert.IsTrue(fixture.HasVisibleText(device.OnlineStateText));

            fixture.State.IsOnline = false;
            await fixture.DeliverAsync(FrontendChangeScope.DevicePresence, token);
            device = fixture.Session.ViewModel.ProfileViewModel.Devices.Single();
            Assert.IsTrue(device.ShowOfflineStatus);
            Assert.IsTrue(fixture.HasVisibleText(device.OnlineStateText));
            Assert.AreEqual(0, fixture.State.ForcedPresenceReads,
                "Automatic presence updates must remain cached reads.");
        });

    [TestMethod]
    public Task ReconnectReconcilesMissedChangesAndDisposedSessionRejectsFurtherDelivery() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            fixture.Agent.Deliver(FrontendChangeStreamEventKind.Disconnected, FrontendChangeScope.Everything);
            fixture.State.PasswordName = "Changed while disconnected";
            await fixture.DeliverAsync(FrontendChangeScope.Everything, token,
                FrontendChangeStreamEventKind.Connected);
            Assert.AreEqual("Changed while disconnected", fixture.Session.ViewModel!.PasswordsViewModel.Passwords.SingleOrDefault()?.Name);
            Assert.IsTrue(fixture.HasVisibleText("Changed while disconnected"));
            await fixture.DisposeSessionAsync();
            Assert.AreEqual(0, fixture.Agent.ReaderCount, "Session disposal must drain its stream reader.");
            var reads = fixture.Transport.Operations.Count;
            fixture.State.PasswordName = "Must not be read after close";
            fixture.Agent.Deliver(FrontendChangeStreamEventKind.Change, FrontendChangeScope.Passwords);
            // Disposal drains the old pump; it cannot admit another endpoint read.
            Assert.AreEqual(reads, fixture.Transport.Operations.Count);
            Assert.IsNull(fixture.Session.View.DataContext);
        });

    private async Task RunScenarioAsync(Func<UiFixture, CancellationToken, Task> scenario)
    {
        var progress = new UiTestProgress(line => TestContext.WriteLine(line));
        var sessionGate = new object();
        HeadlessUnitTestSession? headless = null;
        var shutdownRequested = false;
        await BoundedUiTestRunner.RunAsync(
            async token =>
            {
                progress.Record("Headless session startup.");
                var created = HeadlessUnitTestSession.StartNew(typeof(RefreshTestAppBuilder));
                bool disposeLateStartup;
                lock (sessionGate)
                {
                    disposeLateStartup = shutdownRequested;
                    if (!disposeLateStartup)
                        headless = created;
                }
                if (disposeLateStartup)
                {
                    BoundedUiTestRunner.ObserveFailure(Task.Run(async () => await created.DisposeAsync()));
                    throw new OperationCanceledException(token);
                }

                progress.Record("Headless dispatch queued.");
                await created.Dispatch<bool>(async () =>
                {
                    UiFixture? fixture = null;
                    Exception? failure = null;
                    try
                    {
                        progress.Record("Creating UI fixture.");
                        fixture = new UiFixture(progress);
                        await fixture.InitializeAsync(token);
                        await scenario(fixture, token);
                        progress.Record("Scenario assertions completed.");
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        progress.Record($"UI body failed: {exception.GetType().Name}: {exception.Message}");
                    }
                    finally
                    {
                        if (fixture is not null)
                        {
                            try { await fixture.DisposeAsync(); }
                            catch (Exception exception)
                            {
                                failure = failure is null ? exception : new AggregateException(failure, exception);
                            }
                        }
                    }

                    if (failure is not null)
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    return true;
                // Do not stop the dispatcher frame when the scenario deadline expires:
                // the canceled scenario still needs this UI loop for async cleanup.
                }, CancellationToken.None).ConfigureAwait(false);
                progress.Record("Headless dispatch completed.");
            },
            async () =>
            {
                HeadlessUnitTestSession? current;
                lock (sessionGate)
                {
                    shutdownRequested = true;
                    current = headless;
                }
                if (current is not null)
                    await current.DisposeAsync().ConfigureAwait(false);
            },
            progress,
            scenarioTimeout: TimeSpan.FromSeconds(20),
            completionGrace: TimeSpan.FromSeconds(15),
            shutdownTimeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public static class RefreshTestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI(_ => { });
    }

    private sealed class UiFixture : IAsyncDisposable
    {
        public UiTestProgress Progress { get; }
        public EndpointState State { get; }
        public StreamAgent Agent { get; } = new();
        public RecordingEndpointRpcTransport Transport { get; }
        public WindowsNamedPipeFrontendBackendClient Client { get; }
        public FrontendUiSession Session { get; }
        private readonly Window _window;

        public UiFixture(UiTestProgress progress)
        {
            Progress = progress;
            State = new EndpointState(progress);
            Transport = new RecordingEndpointRpcTransport(State.RespondAsync);
            Client = new WindowsNamedPipeFrontendBackendClient(
                Agent, new FakeEndpointRpcClientConnector(Transport));
            var view = new MainView();
            _window = new Window { Width = 1200, Height = 900, Content = view };
            Session = new FrontendUiSession(view, new FrontendApplicationContext(
                Client, new BackgroundSettings(), Path.GetTempPath(),
                applicationPreferencesStore: new MemoryPreferences()), ownsBackendClient: false);
            _window.Show();
        }

        public async Task InitializeAsync(CancellationToken token)
        {
            Progress.Record("Initializing frontend session.");
            await Session.InitializeAsync().WaitAsync(token);
            Assert.IsNotNull(Session.RefreshCoordinator);
            Assert.IsTrue(Session.ViewModel!.IsAuthenticated, "The controlled remembered session must restore.");
            Progress.Record("Waiting for authenticated stream registration.");
            Assert.AreEqual(State.UserId, await Agent.AuthenticatedStreamStarted.Task.WaitAsync(token));
            await DeliverAsync(FrontendChangeScope.Everything, token);
            Progress.Record("UI fixture initialized.");
        }

        public bool HasVisibleText(string text)
        {
            return _window.GetVisualDescendants().OfType<TextBlock>()
                .Any(control => control.IsEffectivelyVisible && control.Text == text);
        }

        public Task FlushBindingsAsync(CancellationToken token)
        {
            Progress.Record("Waiting for UI binding marker.");
            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Let higher-priority normal, layout and binding work run before inspecting controls.
            UiDispatcher.UIThread.Post(
                () => flushed.TrySetResult(),
                DispatcherPriority.Background);
            return flushed.Task.WaitAsync(token);
        }

        public Task WaitForDevicesLoadedAsync(CancellationToken token)
        {
            Progress.Record("Waiting for initial device-list projection.");
            var viewModel = Session.ViewModel!.ProfileViewModel;
            if (viewModel.AreDevicesLoaded)
                return Task.CompletedTask;

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(viewModel.AreDevicesLoaded) && viewModel.AreDevicesLoaded)
                    loaded.TrySetResult();
            }

            viewModel.PropertyChanged += OnPropertyChanged;
            if (viewModel.AreDevicesLoaded)
                loaded.TrySetResult();
            return AwaitAndDetachAsync();

            async Task AwaitAndDetachAsync()
            {
                try
                {
                    await loaded.Task.WaitAsync(token);
                    Progress.Record("Initial device-list projection completed.");
                }
                finally
                {
                    viewModel.PropertyChanged -= OnPropertyChanged;
                }
            }
        }

        public async Task DeliverAsync(FrontendChangeScope scope, CancellationToken token,
            FrontendChangeStreamEventKind kind = FrontendChangeStreamEventKind.Change)
        {
            // Subscribe before publishing and buffer observations until Deliver returns the
            // actual immutable event. A later watermark in the same runtime also covers it.
            var hints = Channel.CreateBounded<FrontendChangeStreamDelivery>(64);
            void OnHint(FrontendChangeStreamDelivery hint)
            {
                if (!hints.Writer.TryWrite(hint))
                    hints.Writer.TryComplete(new AssertFailedException("The test hint observer overflowed."));
            }
            Session.ChangeHintReceived += OnHint;
            try
            {
                var sent = Agent.Deliver(kind, scope);
                Progress.Record($"Waiting for {kind} hint: runtime={sent.RuntimeInstanceId}, " +
                    $"sequence>={sent.Sequence}, scope={scope}, readers={Agent.ReaderCount}.");
                while (true)
                {
                    var hint = await hints.Reader.ReadAsync(token);
                    Progress.Record($"Observed hint: runtime={hint.RuntimeInstanceId}, sequence={hint.Sequence}.");
                    if (hint.RuntimeInstanceId == sent.RuntimeInstanceId && hint.Sequence >= sent.Sequence)
                        break;
                }
                Progress.Record($"Waiting for refresh coordinator after sequence {sent.Sequence}.");
                await Session.RefreshCoordinator!.WaitForIdleAsync(token);
                await FlushBindingsAsync(token);
                Progress.Record($"Delivery and UI binding barrier completed for sequence {sent.Sequence}.");
            }
            finally
            {
                Session.ChangeHintReceived -= OnHint;
                hints.Writer.TryComplete();
            }
        }

        public Task DisposeSessionAsync() =>
            AwaitCleanupAsync("Frontend session disposal", () => Session.DisposeAsync().AsTask());

        public async ValueTask DisposeAsync()
        {
            Exception? failure = null;
            try { await DisposeSessionAsync(); }
            catch (Exception exception) { failure = exception; }
            try
            {
                Progress.Record("Closing headless window.");
                _window.Close();
                Progress.Record("Headless window closed.");
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            try
            {
                await AwaitCleanupAsync("Windows client disposal", () => Task.Run(async () => await Client.DisposeAsync()));
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private async Task AwaitCleanupAsync(string phase, Func<Task> startCleanup)
        {
            Progress.Record($"{phase} started.");
            var cleanup = startCleanup();
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch
            {
                BoundedUiTestRunner.ObserveFailure(cleanup);
                throw;
            }
            Progress.Record($"{phase} completed.");
        }
    }

    private sealed class EndpointState
    {
        private readonly UiTestProgress _progress;
        private readonly EndpointRpcContractValidator _validator = new();
        public EndpointState(UiTestProgress progress) => _progress = progress;
        private readonly Guid _token = Guid.Parse("10000000-0000-0000-0000-000000000001");
        private readonly Guid _passwordId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        private readonly Guid _deviceId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        public Guid UserId { get; } = Guid.Parse("40000000-0000-0000-0000-000000000004");
        public string PasswordName { get; set; } = "Initial password";
        public string FirstName { get; set; } = "InitialFirstName";
        public string DeviceName { get; set; } = "Initial remote device";
        public bool IsOnline { get; set; }
        public int ForcedPresenceReads { get; private set; }

        public Task<byte[]> RespondAsync(EndpointOperationId operation, byte[] _, CancellationToken token)
        {
            _progress.Record($"Endpoint read: {operation}.");
            token.ThrowIfCancellationRequested();
            object result = operation switch
            {
                EndpointOperationId.RestoreRememberedSessions => new RestoreRememberedSessionsEndpointResponse { Tokens = [_token] },
                EndpointOperationId.GetAuthSessionStatus => new GetAuthSessionStatusEndpointResponse
                {
                    Status = new AuthSessionStatusResponse { IsAuthenticated = true, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) }
                },
                EndpointOperationId.GetUserProfileInfo => new GetUserProfileInfoEndpointResponse
                {
                    Profile = new UserProfileInfoResponse
                    {
                        UId = UserId, Username = "RefreshUser", FirstName = FirstName, LastName = "Fixture",
                        Email = "refresh@example.invalid", RegistrationDate = DateTime.UnixEpoch,
                        RegistrationTimeZoneId = "UTC", RegistrationDeviceType = DeviceType.WindowsPc,
                        IsRememberMeEnabled = true
                    }
                },
                EndpointOperationId.GetSavedPasswords => new GetSavedPasswordsEndpointResponse
                {
                    Passwords = new SavedPasswordsResponse
                    {
                        Passwords = [new PasswordInfoResponse
                        {
                            Id = _passwordId, Name = PasswordName, Color = "#FF2D6AE3",
                            CreatedAt = DateTime.UnixEpoch, LastUpdatedAt = DateTime.UnixEpoch
                        }]
                    }
                },
                EndpointOperationId.GetUserDevices => new GetUserDevicesEndpointResponse { Devices = Devices() },
                EndpointOperationId.RefreshUserDevicePresence => ForcedPresenceResponse(),
                EndpointOperationId.GetLocalDeviceInfo => new GetLocalDeviceInfoEndpointResponse
                {
                    Device = new LocalDeviceInfoResponse { DeviceId = Guid.Parse("50000000-0000-0000-0000-000000000005"),
                        TlsCertFingerprint = "LOCAL", DeviceType = DeviceType.WindowsPc, IsSyncOn = true }
                },
                EndpointOperationId.GetLocalUserSyncOn => new GetLocalUserSyncOnEndpointResponse { IsSyncOn = true },
                _ => throw new AssertFailedException($"Unexpected endpoint operation: {operation}")
            };
            try
            {
                _validator.ValidateResponse(operation, result);
            }
            catch (EndpointRpcPayloadException exception)
            {
                _progress.Record($"Controlled endpoint response rejected for {operation}: {exception.Message}");
                throw;
            }
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(result,
                EndpointRpcJsonContext.Default.GetTypeInfo(result.GetType())!));
        }

        private IReadOnlyList<UserDeviceInfoResponse> Devices() => [new()
        {
            DeviceId = _deviceId, Name = DeviceName, IsOnline = IsOnline, IsTrusted = true,
            IsSyncOn = true, DeviceType = DeviceType.WindowsPc, TlsCertFingerprint = "ABCD", LinkedAt = DateTimeOffset.UnixEpoch
        }];

        private RefreshUserDevicePresenceEndpointResponse ForcedPresenceResponse()
        {
            ForcedPresenceReads++;
            return new() { Devices = Devices() };
        }
    }

    private sealed class StreamAgent : IEndpointRpcAgentConnection, IFrontendChangeStreamClient
    {
        private readonly FakeEndpointRpcAgentConnection _control = new();
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Channel<FrontendChangeStreamEvent>> _readers = new();
        private readonly Guid _runtime = Guid.NewGuid();
        private long _sequence;
        private int _readerCount;
        public int ReaderCount => Volatile.Read(ref _readerCount);
        public TaskCompletionSource<Guid> AuthenticatedStreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => _control.IsConnected;
        public long ConnectionGeneration => _control.ConnectionGeneration;
        public Task Completion => _control.Completion;
        public Task<bool> EnsureConnectedAsync(CancellationToken token = default) => _control.EnsureConnectedAsync(token);
        public Task DisconnectAsync(CancellationToken token = default) => _control.DisconnectAsync(token);
        public Task<bool> PrepareForReplacementAsync(CancellationToken token = default) => _control.PrepareForReplacementAsync(token);
        public Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken token = default) => _control.GetAgentStatusAsync(token);
        public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(CancellationToken token = default) => _control.GetBackendRuntimeStatusAsync(token);
        public Task<DatabaseResetResultDto> ResetDatabaseAsync(CancellationToken token = default) => _control.ResetDatabaseAsync(token);
        public ValueTask DisposeAsync() => _control.DisposeAsync();

        public async IAsyncEnumerable<FrontendChangeStreamEvent> ReadChangeStreamAsync(Guid? userId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<FrontendChangeStreamEvent>(64);
            FrontendChangeStreamEvent connected;
            lock (_gate)
            {
                _readers[id] = channel;
                Volatile.Write(ref _readerCount, _readers.Count);
                // Registration and its Connected watermark describe one atomic point.
                connected = new(FrontendChangeStreamEventKind.Connected, _runtime, _sequence,
                    FrontendChangeScope.Everything, RequiresAuthoritativeRefresh: true);
            }
            try
            {
                if (userId is { } selected)
                    AuthenticatedStreamStarted.TrySetResult(selected);
                yield return connected;
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    yield return item;
            }
            finally
            {
                lock (_gate)
                {
                    _readers.Remove(id);
                    Volatile.Write(ref _readerCount, _readers.Count);
                }
                channel.Writer.TryComplete();
            }
        }

        public FrontendChangeStreamEvent Deliver(FrontendChangeStreamEventKind kind, FrontendChangeScope scope)
        {
            lock (_gate)
            {
                var item = new FrontendChangeStreamEvent(kind, _runtime, ++_sequence, scope,
                    kind != FrontendChangeStreamEventKind.Change);
                foreach (var reader in _readers.Values)
                    Assert.IsTrue(reader.Writer.TryWrite(item));
                return item;
            }
        }
    }

    private sealed class MemoryPreferences : IApplicationPreferencesStore
    {
        private ApplicationPreferences _value = new()
        { Language = AppLanguage.English, Theme = AppThemeId.Dark, InterfaceAnimationsEnabled = false };
        public Task<ApplicationPreferences> ReadAsync(CancellationToken token = default) => Task.FromResult(_value);
        public Task WriteAsync(ApplicationPreferences value, CancellationToken token = default)
        { _value = value; return Task.CompletedTask; }
    }

    private sealed class BackgroundSettings : IBackgroundSyncSettingsClient
    {
        public Task<BackgroundSyncClientState> GetStateAsync(CancellationToken token = default) => Task.FromResult(
            new BackgroundSyncClientState(false, true, false, false, BackgroundSyncClientFailureKind.None, null));
        public Task<BackgroundSyncChangeResult> SetEnabledAsync(bool enabled, CancellationToken token = default) =>
            throw new AssertFailedException("Automatic refresh must not change background sync settings.");
    }
}
