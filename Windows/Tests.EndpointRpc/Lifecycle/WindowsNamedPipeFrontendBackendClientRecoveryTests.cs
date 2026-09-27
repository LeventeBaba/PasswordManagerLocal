using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Lifecycle;

[TestClass]
public sealed class WindowsNamedPipeFrontendBackendClientRecoveryTests
{
    [TestMethod]
    public async Task StartupCompatibilityFailurePreservesResettableFailureKind()
    {
        var agent = new FakeEndpointRpcAgentConnection
        {
            BackendStatus = new BackendRuntimeStatusDto(
                BackendRuntimeStatusState.Failed,
                BackendRuntimeFailureStatusKind.DatabaseCompatibility,
                new IpcFailureDto(
                    IpcFailureKind.Runtime,
                    "The local database version is not supported.",
                    DateTimeOffset.UtcNow,
                    IsRetryable: false,
                    RequiresProcessRestart: false),
                RequiresProcessRestart: false,
                DateTimeOffset.UtcNow,
                new DatabaseCompatibilityStatusDto(
                    DetectedVersion: 99,
                    OldestSupportedVersion: 12,
                    CurrentVersion: 12))
        };
        var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(),
            maximumRecoveryAttempts: 1,
            recoveryDelay: TimeSpan.Zero);

        await Assert.ThrowsExactlyAsync<DatabaseVersionNotSupportedException>(
            () => client.ConnectAsync());

        Assert.AreEqual(BackendRuntimeFailureKind.DatabaseCompatibility, client.Snapshot.FailureKind);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Unavailable, client.ConnectionState);
        var failure = Assert.IsInstanceOfType<DatabaseVersionNotSupportedException>(client.Snapshot.Failure);
        Assert.AreEqual(99, failure.DetectedVersion);
        Assert.AreEqual(12, failure.OldestSupportedVersion);
        Assert.AreEqual(12, failure.CurrentVersion);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task RestartRequiredStatusStopsRecoveryWithoutFallbackRuntime()
    {
        var agent = new FakeEndpointRpcAgentConnection
        {
            BackendStatus = new BackendRuntimeStatusDto(
                BackendRuntimeStatusState.Failed,
                BackendRuntimeFailureStatusKind.ShutdownFailure,
                null,
                RequiresProcessRestart: true,
                DateTimeOffset.UtcNow)
        };
        var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(),
            maximumRecoveryAttempts: 1,
            recoveryDelay: TimeSpan.Zero);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ConnectAsync());

        Assert.AreEqual(BackendRuntimeFailureKind.ShutdownFailure, client.Snapshot.FailureKind);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Unavailable, client.ConnectionState);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointLossRejectsCallsThenRestoresFutureCallsWithoutReplay()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();
        Assert.AreEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);

        first.Disconnect();
        await WaitUntilAsync(() => connector.ConnectCount == 2 && client.Snapshot.State == BackendRuntimeState.Ready);

        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.IsNotNull(await client.GetEndpointsAsync());
        Assert.AreEqual(0, agent.ResetCount);
    }

    [TestMethod]
    public async Task ControlLossRunsOneSerializedRecoveryAndReconnectsBothChannels()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 3,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.Disconnect();
        await WaitUntilAsync(() => client.Snapshot.State == BackendRuntimeState.Ready && connector.ConnectCount == 2);

        Assert.AreEqual(2, agent.EnsureCount);
        Assert.AreEqual(2L, agent.ConnectionGeneration);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.IsTrue(first.IsDisposed);
    }

    [TestMethod]
    public async Task ExistingEndpointConnectionIsRevalidatedBeforeReadyIsRepublished()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var connector = new SequenceEndpointRpcClientConnector(
            new ControllableEndpointRpcTransport());
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 1,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.AgentStatus = agent.AgentStatus with
        {
            AgentState = AgentState.Failed,
            AdmissionState = AgentAdmissionState.Closed,
            IsEndpointHostReady = false,
            LastFailure = new IpcFailureDto(
                IpcFailureKind.AgentShell,
                "The Windows agent failed.",
                DateTimeOffset.UtcNow,
                IsRetryable: true,
                RequiresProcessRestart: false)
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ConnectAsync());

        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreEqual(BackendRuntimeState.Failed, client.Snapshot.State);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Unavailable, client.ConnectionState);
    }

    [TestMethod]
    public async Task FailedSameProcessRemainsUnavailableUntilReplacementPreparationCompletes()
    {
        var replacementReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeEndpointRpcAgentConnection
        {
            ReplacementPreparationCompletion = replacementReady
        };
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.AgentStatus = agent.AgentStatus with
        {
            AgentState = AgentState.Failed,
            AdmissionState = AgentAdmissionState.Closed,
            IsEndpointHostReady = false
        };
        agent.Disconnect();
        await WaitUntilAsync(() => agent.PrepareReplacementCount == 1);

        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreNotEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);

        agent.AgentStatus = HealthyAgentStatus();
        replacementReady.TrySetResult(true);
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);

        Assert.AreEqual(3L, agent.ConnectionGeneration);
    }

    [TestMethod]
    public async Task StoppingSameProcessNeverReconnectsEndpointBeforeOldProcessExit()
    {
        var replacementReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeEndpointRpcAgentConnection
        {
            ReplacementPreparationCompletion = replacementReady
        };
        var connector = new SequenceEndpointRpcClientConnector(
            new ControllableEndpointRpcTransport(),
            new ControllableEndpointRpcTransport());
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.AgentStatus = agent.AgentStatus with
        {
            AgentState = AgentState.Stopping,
            AdmissionState = AgentAdmissionState.Closed,
            IsEndpointHostReady = false
        };
        agent.Disconnect();
        await WaitUntilAsync(() => agent.PrepareReplacementCount == 1);

        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreNotEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);

        agent.AgentStatus = HealthyAgentStatus();
        replacementReady.TrySetResult(true);
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);
    }

    [TestMethod]
    public async Task RestartRequiredSameProcessDoesNotReachReadyOrConnectEndpointEarly()
    {
        var replacementReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeEndpointRpcAgentConnection
        {
            ReplacementPreparationCompletion = replacementReady
        };
        var connector = new SequenceEndpointRpcClientConnector(
            new ControllableEndpointRpcTransport(),
            new ControllableEndpointRpcTransport());
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.AgentStatus = agent.AgentStatus with
        {
            AdmissionState = AgentAdmissionState.Closed,
            RequiresProcessRestart = true,
            IsEndpointHostReady = false
        };
        agent.BackendStatus = agent.BackendStatus with { RequiresProcessRestart = true };
        agent.Disconnect();
        await WaitUntilAsync(() => agent.PrepareReplacementCount == 1);

        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreNotEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);

        agent.AgentStatus = HealthyAgentStatus();
        agent.BackendStatus = agent.BackendStatus with { RequiresProcessRestart = false };
        replacementReady.TrySetResult(true);
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);
    }

    [TestMethod]
    public async Task FailedRecoveryIsBoundedAndLeavesClientUnavailable()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        agent.EnqueueEnsureResult(true);
        agent.EnqueueEnsureResult(false);
        agent.EnqueueEnsureResult(false);
        agent.EnqueueEnsureResult(false);
        var first = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 3,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.Disconnect();
        await WaitUntilAsync(() => agent.EnsureCount == 4);

        Assert.AreEqual(BackendRuntimeState.Failed, client.Snapshot.State);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Unavailable, client.ConnectionState);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetEndpointsAsync());
        Assert.AreEqual(1, connector.ConnectCount);
    }

    [TestMethod]
    public async Task DisposalCancelsAndAwaitsTheSingleRecoveryLoop()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        agent.EnqueueEnsureResult(true);
        agent.EnqueueEnsureResult(false);
        var transport = new ControllableEndpointRpcTransport();
        var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(transport),
            maximumRecoveryAttempts: 3,
            recoveryDelay: TimeSpan.FromSeconds(30));
        await client.ConnectAsync();

        agent.Disconnect();
        await WaitUntilAsync(() => agent.EnsureCount == 2);
        await client.DisposeAsync();

        Assert.AreEqual(1, agent.DisposeCount);
        Assert.IsTrue(transport.IsDisposed);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Disposed, client.ConnectionState);
    }

    [TestMethod]
    public async Task AgentOrchestratedResetInvalidatesOldEndpointAndReconnects()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        await client.ResetDatabaseAndRestartAsync();

        Assert.AreEqual(1, agent.ResetCount);
        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.AreEqual(BackendRuntimeState.Ready, client.Snapshot.State);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);
    }

    [TestMethod]
    public async Task RejectedResetReconnectsEndpointWithoutReplayingReset()
    {
        var agent = new FakeEndpointRpcAgentConnection
        {
            ResetResult = new(false, false, "The reset was rejected before shutdown.")
        };
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.ResetDatabaseAndRestartAsync());
        await WaitUntilAsync(() => connector.ConnectCount == 2 && client.Snapshot.State == BackendRuntimeState.Ready);

        Assert.AreEqual(1, agent.ResetCount);
        Assert.IsTrue(first.IsDisposed);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task ResetRequiringProcessRestartRecoversThroughCleanAgentWithoutReplay()
    {
        var agent = new FakeEndpointRpcAgentConnection
        {
            ResetResult = new(false, true, "The agent must restart."),
            DisconnectOnReset = true
        };
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.ResetDatabaseAndRestartAsync());
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);

        Assert.AreEqual(1, agent.ResetCount);
        Assert.AreEqual(1, agent.PrepareReplacementCount);
        Assert.AreEqual(2L, agent.ConnectionGeneration);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.IsTrue(first.IsDisposed);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }


    [TestMethod]
    public async Task RestartRequiredResetWaitsForOldAgentTerminationBeforeRecovery()
    {
        var replacementReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeEndpointRpcAgentConnection
        {
            ResetResult = new(false, true, "The agent must restart."),
            ReplacementPreparationCompletion = replacementReady
        };
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        var reset = client.ResetDatabaseAndRestartAsync();
        await WaitUntilAsync(() => agent.PrepareReplacementCount == 1);

        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreEqual(1, agent.EnsureCount);

        replacementReady.TrySetResult(true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reset);
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);

        Assert.AreEqual(2, agent.EnsureCount);
    }

    [TestMethod]
    public async Task RestartRequiredExitWaitTimeoutDoesNotLaunchRecovery()
    {
        var agent = new FakeEndpointRpcAgentConnection
        {
            ResetResult = new(false, true, "The old agent did not exit."),
            ReplacementPreparationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        agent.ReplacementPreparationCompletion.TrySetResult(false);
        var connector = new SequenceEndpointRpcClientConnector(
            new ControllableEndpointRpcTransport());
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.ResetDatabaseAndRestartAsync());
        await Task.Delay(25);

        Assert.AreEqual(1, agent.PrepareReplacementCount);
        Assert.AreEqual(1, agent.EnsureCount);
        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Unavailable, client.ConnectionState);
    }

    [TestMethod]
    public async Task IntentionalShutdownSuppressesRecoveryBeforeBothConnectionsClose()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var transport = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(transport);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        var acknowledged = await client.BeginIntentionalAgentShutdownAsync();
        transport.Disconnect();
        agent.Disconnect();
        await Task.Delay(25);

        Assert.IsTrue(acknowledged);
        Assert.AreEqual(WindowsFrontendRecoverySuppressionState.IntentionalShutdown, client.RecoverySuppressionState);
        Assert.AreEqual(WindowsEndpointClientConnectionState.IntentionalShutdown, client.ConnectionState);
        Assert.AreEqual(1, connector.ConnectCount);
        Assert.AreEqual(1, agent.EnsureCount);
        Assert.AreEqual(1, agent.DisconnectCount);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task RepeatedIntentionalShutdownRequestsAreIdempotent()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(new ControllableEndpointRpcTransport()),
            maximumRecoveryAttempts: 1,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        var first = await client.BeginIntentionalAgentShutdownAsync();
        var second = await client.BeginIntentionalAgentShutdownAsync();

        Assert.IsTrue(first);
        Assert.IsTrue(second);
        Assert.AreEqual(1, agent.DisconnectCount);
    }

    [TestMethod]
    public async Task RejectedIntentionalShutdownRestoresNormalRecovery()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();
        await client.BeginIntentionalAgentShutdownAsync();

        await client.CancelIntentionalAgentShutdownAsync();
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);

        Assert.AreEqual(WindowsFrontendRecoverySuppressionState.None, client.RecoverySuppressionState);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task IntentionalShutdownDuringDatabaseResetIsRejectedWithoutPermanentSuppression()
    {
        var resetCompletion = new TaskCompletionSource<DatabaseResetResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeEndpointRpcAgentConnection { ResetCompletion = resetCompletion };
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(first, second),
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        var reset = client.ResetDatabaseAndRestartAsync();
        await WaitUntilAsync(() => agent.ResetCount == 1);
        var acknowledged = await client.BeginIntentionalAgentShutdownAsync();
        resetCompletion.TrySetResult(new DatabaseResetResultDto(true, false, null));
        await reset;

        Assert.IsFalse(acknowledged);
        Assert.AreEqual(WindowsFrontendRecoverySuppressionState.None, client.RecoverySuppressionState);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);

        Assert.IsTrue(await client.BeginIntentionalAgentShutdownAsync());
        Assert.AreEqual(WindowsFrontendRecoverySuppressionState.IntentionalShutdown, client.RecoverySuppressionState);
    }

    [TestMethod]
    public async Task RepeatedRecoveryRetainsOnlyCurrentEndpointAndControlObservers()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var transports = Enumerable.Range(0, 6)
            .Select(_ => new ControllableEndpointRpcTransport())
            .ToArray();
        var connector = new SequenceEndpointRpcClientConnector(transports);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        for (var index = 0; index < 5; index++)
        {
            transports[index].Disconnect();
            var expectedConnectCount = index + 2;
            await WaitUntilAsync(() => connector.ConnectCount == expectedConnectCount &&
                client.ConnectionState == WindowsEndpointClientConnectionState.Ready &&
                client.ActiveObserverCount == 2);
            Assert.AreEqual(2, client.ActiveObserverCount);
        }
    }

    [TestMethod]
    public async Task StaleEndpointObserverCannotAffectNewGeneration()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var stale = new DelayedCompletionEndpointRpcTransport();
        var current = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(stale, current);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        agent.Disconnect();
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);
        stale.Complete(new IOException("stale completion"));
        await WaitUntilAsync(() => client.ActiveObserverCount == 2);

        Assert.AreEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task FaultedEndpointObserverIsObservedRemovedAndReplaced()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        first.Disconnect(new IOException("observer failure"));
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready &&
            client.ActiveObserverCount == 2);

        Assert.AreEqual(2, client.ActiveObserverCount);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task StaleControlObserverCannotAffectNewConnectionGeneration()
    {
        var agent = new FakeEndpointRpcAgentConnection();
        var first = new ControllableEndpointRpcTransport();
        var second = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(first, second);
        await using var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            connector,
            maximumRecoveryAttempts: 2,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();

        var staleControlCompletion = agent.ReplaceConnectionWithoutCompletingPrevious();
        first.Disconnect();
        await WaitUntilAsync(() => connector.ConnectCount == 2 &&
            client.ConnectionState == WindowsEndpointClientConnectionState.Ready);
        staleControlCompletion.TrySetException(new IOException("stale control completion"));
        await WaitUntilAsync(() => client.ActiveObserverCount == 2);

        Assert.AreEqual(WindowsEndpointClientConnectionState.Ready, client.ConnectionState);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task DisposalCancelsObserversWhoseTransportCompletionNeverFinishes()
    {
        var transport = new DelayedCompletionEndpointRpcTransport();
        var agent = new FakeEndpointRpcAgentConnection();
        var client = new WindowsNamedPipeFrontendBackendClient(
            agent,
            new SequenceEndpointRpcClientConnector(transport),
            maximumRecoveryAttempts: 1,
            recoveryDelay: TimeSpan.Zero);
        await client.ConnectAsync();
        Assert.AreEqual(2, client.ActiveObserverCount);

        await client.DisposeAsync();

        Assert.AreEqual(0, client.ActiveObserverCount);
        Assert.IsTrue(transport.IsDisposed);
        Assert.AreEqual(WindowsEndpointClientConnectionState.Disposed, client.ConnectionState);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static AgentStatusDto HealthyAgentStatus() => new(
        AgentState.Running,
        AgentAdmissionState.Open,
        IsUiConnected: true,
        BackendOwnedByAgent: true,
        IsBackendRunning: true,
        IsBackgroundSyncEnabled: false,
        RequiresProcessRestart: false,
        LastFailure: null,
        StartedAtUtc: DateTimeOffset.UtcNow,
        IsEndpointHostReady: true);
}
