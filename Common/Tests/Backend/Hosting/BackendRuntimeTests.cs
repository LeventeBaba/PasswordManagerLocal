using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class BackendRuntimeTests
{
    [TestMethod]
    public void InitialSnapshot_IsNotStarted()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(directory.Path, _ => Task.CompletedTask, new FakeSyncRuntimeService(), out _);

        Assert.AreEqual(BackendRuntimeState.NotStarted, runtime.Snapshot.State);
        Assert.AreEqual(BackendRuntimeFailureKind.None, runtime.Snapshot.FailureKind);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
    }

    [TestMethod]
    public async Task ConcurrentEnsureStarted_CreatesOneHost()
    {
        using var directory = new TemporaryDirectory();
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = CreateRuntime(
            directory.Path,
            _ => releaseInitialization.Task,
            new FakeSyncRuntimeService(),
            out var hostCreations);

        var starts = Enumerable.Range(0, 10)
            .Select(_ => runtime.EnsureStartedAsync())
            .ToArray();

        await WaitForAsync(() => hostCreations() == 1);
        releaseInitialization.SetResult();
        await Task.WhenAll(starts);

        Assert.AreEqual(1, hostCreations());
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task CallerCancellation_DoesNotCancelSharedStartup()
    {
        using var directory = new TemporaryDirectory();
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = CreateRuntime(
            directory.Path,
            _ => releaseInitialization.Task,
            new FakeSyncRuntimeService(),
            out _);
        using var cancellation = new CancellationTokenSource();

        var cancelledWaiter = runtime.EnsureStartedAsync(cancellation.Token);
        var survivingWaiter = runtime.EnsureStartedAsync();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWaiter);
        releaseInitialization.SetResult();
        await survivingWaiter;

        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task FailedStartup_CanBeRetriedWithANewHost()
    {
        using var directory = new TemporaryDirectory();
        var attempts = 0;
        var runtime = CreateRuntime(
            directory.Path,
            _ => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("first failure"))
                : Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out var hostCreations);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.EnsureStartedAsync());
        Assert.AreEqual(BackendRuntimeState.Failed, runtime.Snapshot.State);

        await runtime.EnsureStartedAsync();

        Assert.AreEqual(2, hostCreations());
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task DeviceLockedFailure_TransitionsToWaitingAndCanRetry()
    {
        using var directory = new TemporaryDirectory();
        var keyAvailable = false;
        var hostCreations = 0;
        var paths = new BackendStoragePaths(directory.Path);
        var options = new BackendRuntimeOptions(
            paths,
            () => keyAvailable
                ? new TestKeyProtector()
                : throw new KeyProtectorUnavailableException(KeyProtectorUnavailableReason.DeviceLocked),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) =>
            {
                hostCreations++;
                return CreateHost(new DelegateInitializationService(_ => Task.CompletedTask), new FakeSyncRuntimeService());
            });
        var runtime = new BackendRuntime(options);

        await Assert.ThrowsAsync<KeyProtectorUnavailableException>(async () => await runtime.EnsureStartedAsync());
        Assert.AreEqual(BackendRuntimeState.WaitingForDeviceUnlock, runtime.Snapshot.State);

        keyAvailable = true;
        await runtime.EnsureStartedAsync();

        Assert.AreEqual(1, hostCreations);
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task SyncFailure_LeavesBackendReadyAndMarksSyncDegraded()
    {
        using var directory = new TemporaryDirectory();
        var syncFailure = new InvalidOperationException("sync failed");
        var syncRuntime = new FakeSyncRuntimeService { RefreshFailure = syncFailure };
        var runtime = CreateRuntime(directory.Path, _ => Task.CompletedTask, syncRuntime, out _);

        await runtime.EnsureStartedAsync();
        await WaitForAsync(() => runtime.SyncSnapshot.State == SyncRuntimeState.Degraded);

        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        Assert.AreSame(syncFailure, runtime.SyncSnapshot.Failure);
        await using var session = await runtime.OpenInteractiveSessionAsync();
        Assert.IsNotNull(session.Endpoints);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task InteractiveSession_OpensOnlyAfterReadinessAndIsExclusive()
    {
        using var directory = new TemporaryDirectory();
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = CreateRuntime(
            directory.Path,
            _ => releaseInitialization.Task,
            new FakeSyncRuntimeService(),
            out _);

        var sessionTask = runtime.OpenInteractiveSessionAsync();
        Assert.IsFalse(sessionTask.IsCompleted);
        releaseInitialization.SetResult();

        await using var session = await sessionTask;
        Assert.IsNotNull(session.Endpoints);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.Active,
            runtime.InteractiveSessionSnapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        await session.DisposeAsync();
        await using var reopenedSession = await runtime.OpenInteractiveSessionAsync();
        Assert.IsNotNull(reopenedSession.Endpoints);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task OpeningStateRejectsConcurrentInteractiveAttachment()
    {
        using var directory = new TemporaryDirectory();
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactiveHostedService = new FakeInteractiveBackendHostedService
        {
            StartBlock = releaseStart.Task
        };
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveHostedService: interactiveHostedService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();

        var opening = runtime.OpenInteractiveSessionAsync();
        await WaitForAsync(() =>
            runtime.InteractiveSessionSnapshot.State == InteractiveSessionLifecycleState.Opening);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        releaseStart.TrySetResult();
        var session = await opening;
        Assert.AreEqual(
            InteractiveSessionLifecycleState.Active,
            runtime.InteractiveSessionSnapshot.State);

        await session.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task CancellationDuringOpeningCleansPartialStateAndAllowsRetry()
    {
        using var directory = new TemporaryDirectory();
        var neverComplete = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostedService = new FakeInteractiveBackendHostedService
        {
            StartBlock = neverComplete.Task
        };
        var resetter = new FakeInteractiveSensitiveStateResetter();
        var sessionState = new FakeInteractiveSessionStateService(isActive: false);
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveResetter: resetter,
                interactiveSessionState: sessionState,
                interactiveHostedService: hostedService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();
        using var cancellation = new CancellationTokenSource();

        var opening = runtime.OpenInteractiveSessionAsync(cancellation.Token);
        await WaitForAsync(() =>
            runtime.InteractiveSessionSnapshot.State == InteractiveSessionLifecycleState.Opening);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await opening);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
        Assert.IsFalse(sessionState.IsActive);
        Assert.IsTrue(hostedService.StopCalls >= 1);
        Assert.IsTrue(resetter.ResetCalls >= 1);

        hostedService.StartBlock = Task.CompletedTask;
        var session = await runtime.OpenInteractiveSessionAsync();
        Assert.AreEqual(
            InteractiveSessionLifecycleState.Active,
            runtime.InteractiveSessionSnapshot.State);
        await session.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task RuntimeStopDuringOpeningCannotPublishAnActiveSession()
    {
        using var directory = new TemporaryDirectory();
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostedService = new FakeInteractiveBackendHostedService
        {
            StartBlock = releaseStart.Task
        };
        var resetter = new FakeInteractiveSensitiveStateResetter();
        var sessionState = new FakeInteractiveSessionStateService(isActive: false);
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveResetter: resetter,
                interactiveSessionState: sessionState,
                interactiveHostedService: hostedService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();

        var opening = runtime.OpenInteractiveSessionAsync();
        await WaitForAsync(() =>
            runtime.InteractiveSessionSnapshot.State == InteractiveSessionLifecycleState.Opening);
        var stopping = runtime.StopAsync();
        await WaitForAsync(() => runtime.Snapshot.State == BackendRuntimeState.Stopping);
        releaseStart.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await opening);
        await stopping;
        Assert.AreEqual(BackendRuntimeState.Stopped, runtime.Snapshot.State);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
        Assert.IsFalse(runtime.InteractiveSessionSnapshot.AcceptsOperations);
        Assert.IsFalse(sessionState.IsActive);

        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task BackgroundStartupDoesNotResolveEndpointsOrActivateInteractiveState()
    {
        using var directory = new TemporaryDirectory();
        var endpointResolutions = 0;
        var sessionState = new FakeInteractiveSessionStateService(isActive: false);
        var resetter = new FakeInteractiveSensitiveStateResetter();
        var syncRuntime = new FakeSyncRuntimeService();
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                syncRuntime,
                () => endpointResolutions++,
                resetter,
                sessionState));
        var runtime = new BackendRuntime(options);

        await runtime.EnsureStartedAsync();

        Assert.AreEqual(0, endpointResolutions);
        Assert.IsFalse(sessionState.IsActive);

        var session = await runtime.OpenInteractiveSessionAsync();
        var endpoints = session.Endpoints;
        Assert.AreEqual(1, endpointResolutions);
        Assert.IsTrue(sessionState.IsActive);

        await session.DisposeAsync();
        Assert.IsFalse(sessionState.IsActive);
        Assert.AreEqual(1, resetter.ResetCalls);
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        Assert.AreEqual(0, syncRuntime.StopCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await endpoints.GetLocalDeviceInfoAsync());

        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task InteractiveEndpointFacadeIsResolvedForEachSession()
    {
        using var directory = new TemporaryDirectory();
        var endpointResolutions = 0;
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                () => endpointResolutions++));
        var runtime = new BackendRuntime(options);

        await runtime.EnsureStartedAsync();
        await using (var firstSession = await runtime.OpenInteractiveSessionAsync())
            Assert.IsNotNull(firstSession.Endpoints);

        await using (var secondSession = await runtime.OpenInteractiveSessionAsync())
            Assert.IsNotNull(secondSession.Endpoints);

        Assert.AreEqual(2, endpointResolutions);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task BackgroundOnlyShutdownResetsInteractiveSensitiveStateWithoutResolvingEndpoints()
    {
        using var directory = new TemporaryDirectory();
        var endpointResolutions = 0;
        var resetter = new FakeInteractiveSensitiveStateResetter();
        var sessionState = new FakeInteractiveSessionStateService(isActive: false);
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                () => endpointResolutions++,
                resetter,
                sessionState));
        var runtime = new BackendRuntime(options);

        await runtime.EnsureStartedAsync();
        await runtime.StopAsync();

        Assert.AreEqual(0, endpointResolutions);
        Assert.AreEqual(1, resetter.ResetCalls);
        Assert.IsFalse(sessionState.IsActive);
        Assert.AreEqual(BackendRuntimeState.Stopped, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task FailedInteractiveSessionCreationResetsPartialInteractiveState()
    {
        using var directory = new TemporaryDirectory();
        var startFailure = new InvalidOperationException("interactive startup failed");
        var interactiveHostedService = new FakeInteractiveBackendHostedService
        {
            StartFailure = startFailure
        };
        var sessionState = new FakeInteractiveSessionStateService(isActive: false);
        var resetter = new FakeInteractiveSensitiveStateResetter();
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveResetter: resetter,
                interactiveSessionState: sessionState,
                interactiveHostedService: interactiveHostedService));
        var runtime = new BackendRuntime(options);

        await runtime.EnsureStartedAsync();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        Assert.AreSame(startFailure, thrown);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
        Assert.IsFalse(sessionState.IsActive);
        Assert.AreEqual(1, resetter.ResetCalls);
        Assert.AreEqual(1, interactiveHostedService.StartCalls);
        Assert.AreEqual(1, interactiveHostedService.StopCalls);

        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task InteractiveClosingRejectsNewCallsAndWaitsForAdmittedOperation()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);
        await runtime.EnsureStartedAsync();
        var session = (InteractiveBackendSession)await runtime.OpenInteractiveSessionAsync();
        var endpoints = session.Endpoints;
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = session.ExecuteAsync(async _ =>
        {
            operationStarted.TrySetResult();
            await releaseOperation.Task;
        });

        await operationStarted.Task;
        var disposal = session.DisposeAsync().AsTask();
        await WaitForAsync(() =>
            runtime.InteractiveSessionSnapshot.State == InteractiveSessionLifecycleState.Closing);

        Assert.IsFalse(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await endpoints.GetLocalDeviceInfoAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        releaseOperation.TrySetResult();
        await operation;
        await disposal;
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task CleanupFailureIsFailClosedUntilCoordinatorRecoveryCompletes()
    {
        using var directory = new TemporaryDirectory();
        var cleanupFailure = new InvalidOperationException("interactive reset failed");
        var hostCreations = 0;
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) =>
            {
                hostCreations++;
                return CreateHost(
                    new DelegateInitializationService(_ => Task.CompletedTask),
                    new FakeSyncRuntimeService(),
                    interactiveResetter: new FakeInteractiveSensitiveStateResetter
                    {
                        ResetFailure = hostCreations == 1 ? cleanupFailure : null
                    },
                    interactiveSessionState: new FakeInteractiveSessionStateService(isActive: false));
            });
        var runtime = new BackendRuntime(options);
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var backgroundLease = await coordinator.AcquireAsync(
            BackendLifetimeReason.BackgroundSync);
        await using var interactiveLease = await coordinator.AcquireAsync(
            BackendLifetimeReason.InteractiveUi);
        var session = await runtime.OpenInteractiveSessionAsync();

        await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());

        Assert.AreEqual(
            InteractiveSessionLifecycleState.CleanupFailed,
            runtime.InteractiveSessionSnapshot.State);
        Assert.IsTrue(runtime.InteractiveSessionSnapshot.RequiresRecovery);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        await coordinator.RecoverRuntimeAsync();

        Assert.AreEqual(2, hostCreations);
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            coordinator.ActiveReasons);

        await using var recoveredSession = await runtime.OpenInteractiveSessionAsync();
        Assert.IsNotNull(recoveredSession.Endpoints);
    }

    [TestMethod]
    public async Task MultipleInteractiveCleanupFailuresAggregateAndDuplicateDisposalIsSafe()
    {
        using var directory = new TemporaryDirectory();
        var hostedStopFailure = new InvalidOperationException("interactive hosted stop failed");
        var resetFailure = new InvalidOperationException("interactive reset failed");
        var interactiveHostedService = new FakeInteractiveBackendHostedService
        {
            StopFailure = hostedStopFailure
        };
        var resetter = new FakeInteractiveSensitiveStateResetter
        {
            ResetFailure = resetFailure
        };
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveResetter: resetter,
                interactiveSessionState: new FakeInteractiveSessionStateService(isActive: false),
                interactiveHostedService: interactiveHostedService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();
        var session = await runtime.OpenInteractiveSessionAsync();

        await Assert.ThrowsAsync<AggregateException>(
            async () => await session.DisposeAsync());

        Assert.AreEqual(
            InteractiveSessionLifecycleState.CleanupFailed,
            runtime.InteractiveSessionSnapshot.State);
        var recordedFailure = runtime.InteractiveSessionSnapshot.Failure as AggregateException;
        Assert.IsNotNull(recordedFailure);
        var flattenedFailures = recordedFailure.Flatten().InnerExceptions;
        CollectionAssert.Contains(flattenedFailures.ToList(), hostedStopFailure);
        CollectionAssert.Contains(flattenedFailures.ToList(), resetFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        var stopCalls = interactiveHostedService.StopCalls;
        var resetCalls = resetter.ResetCalls;
        await Assert.ThrowsAsync<AggregateException>(
            async () => await session.DisposeAsync());
        Assert.AreEqual(stopCalls, interactiveHostedService.StopCalls);
        Assert.AreEqual(resetCalls, resetter.ResetCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.DisposeAsync());
    }

    [TestMethod]
    public async Task FailedOpeningWithCleanupFailureDoesNotLeaveActiveSession()
    {
        using var directory = new TemporaryDirectory();
        var startFailure = new InvalidOperationException("interactive start failed");
        var resetFailure = new InvalidOperationException("interactive reset failed");
        var interactiveHostedService = new FakeInteractiveBackendHostedService
        {
            StartFailure = startFailure
        };
        var resetter = new FakeInteractiveSensitiveStateResetter
        {
            ResetFailure = resetFailure
        };
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                new FakeSyncRuntimeService(),
                interactiveResetter: resetter,
                interactiveSessionState: new FakeInteractiveSessionStateService(isActive: false),
                interactiveHostedService: interactiveHostedService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();

        await Assert.ThrowsAsync<AggregateException>(
            async () => await runtime.OpenInteractiveSessionAsync());

        Assert.AreEqual(
            InteractiveSessionLifecycleState.CleanupFailed,
            runtime.InteractiveSessionSnapshot.State);
        Assert.IsFalse(runtime.InteractiveSessionSnapshot.AcceptsOperations);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.OpenInteractiveSessionAsync());
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task ResetAfterCompatibilityFailure_DeletesStorageAndRestarts()
    {
        using var directory = new TemporaryDirectory();
        var attempts = 0;
        var runtime = CreateRuntime(
            directory.Path,
            _ => ++attempts == 1
                ? Task.FromException(new DatabaseVersionNotSupportedException(99, 1, 1))
                : Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);

        await Assert.ThrowsAsync<DatabaseVersionNotSupportedException>(async () => await runtime.EnsureStartedAsync());

        var paths = new BackendStoragePaths(directory.Path);
        foreach (var path in new[]
                 {
                     paths.DatabasePath,
                     paths.DatabaseWalPath,
                     paths.DatabaseShmPath,
                     paths.DatabaseJournalPath,
                     paths.DatabaseConfigPath,
                     $"{paths.DatabaseConfigPath}.123.tmp"
                 })
        {
            await File.WriteAllTextAsync(path, "test");
        }

        await runtime.ResetDatabaseAndRestartAsync();

        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        Assert.IsFalse(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.DatabaseWalPath));
        Assert.IsFalse(File.Exists(paths.DatabaseShmPath));
        Assert.IsFalse(File.Exists(paths.DatabaseJournalPath));
        Assert.IsFalse(File.Exists(paths.DatabaseConfigPath));
        Assert.IsFalse(File.Exists($"{paths.DatabaseConfigPath}.123.tmp"));
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task SuccessfulStartup_RaisesStartingThenReady()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);
        var states = new List<BackendRuntimeState>();
        runtime.StateChanged += (_, args) => states.Add(args.Current.State);

        await runtime.EnsureStartedAsync();

        CollectionAssert.AreEqual(
            new[] { BackendRuntimeState.Starting, BackendRuntimeState.Ready },
            states);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task ThrowingStateObserver_DoesNotBreakStartup()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);
        runtime.StateChanged += (_, _) => throw new InvalidOperationException("observer failure");

        await runtime.EnsureStartedAsync();

        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task RuntimeShutdownRejectsNewWorkAndDrainsAdmittedInteractiveOperation()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);
        await runtime.EnsureStartedAsync();
        var session = (InteractiveBackendSession)await runtime.OpenInteractiveSessionAsync();
        var endpoints = session.Endpoints;
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = session.ExecuteAsync(async _ =>
        {
            operationStarted.TrySetResult();
            await releaseOperation.Task;
        });

        await operationStarted.Task;
        var stopping = runtime.StopAsync();
        await WaitForAsync(() =>
            runtime.InteractiveSessionSnapshot.State == InteractiveSessionLifecycleState.Closing);

        Assert.IsFalse(stopping.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await endpoints.GetLocalDeviceInfoAsync());

        releaseOperation.TrySetResult();
        await operation;
        await stopping;
        Assert.AreEqual(BackendRuntimeState.Stopped, runtime.Snapshot.State);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);

        await session.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task RuntimeShutdownContinuesAfterInteractiveResetFailure()
    {
        using var directory = new TemporaryDirectory();
        var resetFailure = new InvalidOperationException("interactive reset failed");
        var resetter = new FakeInteractiveSensitiveStateResetter
        {
            ResetFailure = resetFailure
        };
        var syncRuntime = new FakeSyncRuntimeService();
        var calls = new List<string>();
        var coreService = new FakeBackendHostedService("core", calls);
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) => CreateHost(
                new DelegateInitializationService(_ => Task.CompletedTask),
                syncRuntime,
                interactiveResetter: resetter,
                backendHostedService: coreService));
        var runtime = new BackendRuntime(options);
        await runtime.EnsureStartedAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.StopAsync());

        Assert.AreSame(resetFailure, thrown);
        Assert.AreEqual(1, syncRuntime.StopCalls);
        CollectionAssert.Contains(calls, "stop:core");
        Assert.AreEqual(BackendRuntimeState.Failed, runtime.Snapshot.State);
        Assert.AreEqual(BackendRuntimeFailureKind.ShutdownFailure, runtime.Snapshot.FailureKind);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.CleanupFailed,
            runtime.InteractiveSessionSnapshot.State);

        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task StopFailureLeavesRuntimeInTruthfulFailedState()
    {
        using var directory = new TemporaryDirectory();
        var calls = new List<string>();
        var hostedService = new FakeBackendHostedService(
            "failing-stop",
            calls,
            throwOnStop: true);
        var hostCreations = 0;
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory.Path),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) =>
            {
                hostCreations++;
                return CreateHost(
                    new DelegateInitializationService(_ => Task.CompletedTask),
                    new FakeSyncRuntimeService(),
                    backendHostedService: hostedService);
            });
        var runtime = new BackendRuntime(options);

        await runtime.EnsureStartedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.StopAsync());

        Assert.AreEqual(BackendRuntimeState.Failed, runtime.Snapshot.State);
        Assert.AreEqual(BackendRuntimeFailureKind.ShutdownFailure, runtime.Snapshot.FailureKind);
        CollectionAssert.Contains(calls, "stop:failing-stop");

        var restartFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.EnsureStartedAsync());
        StringAssert.Contains(restartFailure.Message, "Runtime recreation is required");
        Assert.AreEqual(1, hostCreations);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.DisposeAsync());
    }

    [TestMethod]
    public async Task StopThenStart_CreatesANewHost()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out var hostCreations);

        await runtime.EnsureStartedAsync();
        await runtime.StopAsync();
        await runtime.EnsureStartedAsync();

        Assert.AreEqual(2, hostCreations());
        Assert.AreEqual(BackendRuntimeState.Ready, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    [TestMethod]
    public async Task ResetWhileHealthy_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            _ => Task.CompletedTask,
            new FakeSyncRuntimeService(),
            out _);
        await runtime.EnsureStartedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.ResetDatabaseAndRestartAsync());

        await runtime.DisposeAsync();
    }

    [TestMethod]
    public void RuntimeCreation_RejectsNullRequiredDependencies()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);

        Assert.Throws<ArgumentNullException>(() => BackendRuntimeFactory.Create(
            null!,
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease()));
        Assert.Throws<ArgumentNullException>(() => BackendRuntimeFactory.Create(
            paths,
            null!,
            static () => new FakeLocalDiscoveryNetworkLease()));
        Assert.Throws<ArgumentNullException>(() => BackendRuntimeFactory.Create(
            paths,
            static () => new TestKeyProtector(),
            null!));
    }

    [TestMethod]
    public async Task FactoryReturningNull_FailsStartup()
    {
        using var directory = new TemporaryDirectory();
        var runtime = BackendRuntimeFactory.Create(
            new BackendStoragePaths(directory.Path),
            static () => null!,
            static () => new FakeLocalDiscoveryNetworkLease());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.EnsureStartedAsync());

        Assert.AreEqual(BackendRuntimeState.Failed, runtime.Snapshot.State);
        await runtime.DisposeAsync();
    }

    private static BackendRuntime CreateRuntime(
        string directory,
        Func<CancellationToken, Task> initialize,
        FakeSyncRuntimeService syncRuntime,
        out Func<int> hostCreationCount)
    {
        var hostCreations = 0;
        var options = new BackendRuntimeOptions(
            new BackendStoragePaths(directory),
            static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(),
            (_, _) =>
            {
                Interlocked.Increment(ref hostCreations);
                return CreateHost(new DelegateInitializationService(initialize), syncRuntime);
            });
        hostCreationCount = () => Volatile.Read(ref hostCreations);
        return new BackendRuntime(options);
    }

    private static BackendServiceHost CreateHost(
        IBackendInitializationService initialization,
        ISyncRuntimeService syncRuntime,
        Action? endpointsResolved = null,
        IInteractiveSensitiveStateResetter? interactiveResetter = null,
        IInteractiveSessionStateService? interactiveSessionState = null,
        IInteractiveBackendHostedService? interactiveHostedService = null,
        IBackendHostedService? backendHostedService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBackendInitializationService>(initialization);
        services.AddSingleton<ISyncRuntimeService>(syncRuntime);
        services.AddTransient<IEndpoints>(provider =>
        {
            endpointsResolved?.Invoke();
            return new Endpoints(provider.GetRequiredService<IServiceScopeFactory>());
        });
        services.AddSingleton<IInteractiveSensitiveStateResetter>(
            interactiveResetter ?? new FakeInteractiveSensitiveStateResetter());

        services.AddSingleton<IInteractiveSessionStateService>(
            interactiveSessionState ?? new FakeInteractiveSessionStateService(isActive: false));

        if (interactiveHostedService is not null)
            services.AddSingleton<IInteractiveBackendHostedService>(interactiveHostedService);

        if (backendHostedService is not null)
            services.AddSingleton<IBackendHostedService>(backendHostedService);

        return new BackendServiceHost(services.BuildServiceProvider());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
                Assert.Fail("The expected state was not reached.");

            await Task.Delay(10);
        }
    }

    private sealed class DelegateInitializationService : IBackendInitializationService
    {
        private readonly Func<CancellationToken, Task> _initialize;

        public DelegateInitializationService(Func<CancellationToken, Task> initialize)
        {
            _initialize = initialize;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            _initialize(cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PasswordManagerLocal.Common.Testss.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
