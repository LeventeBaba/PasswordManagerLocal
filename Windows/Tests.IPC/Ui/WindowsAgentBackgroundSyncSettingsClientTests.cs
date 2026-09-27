using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Frontend.Settings;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsAgentBackgroundSyncSettingsClientTests
{
    [TestMethod]
    public async Task LoadReturnsAuthoritativeAgentState()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            BackgroundState = FakeWindowsBackgroundSyncCoordinator.OperationalState()
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var state = await client.GetStateAsync();

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsAvailable);
        Assert.IsFalse(state.IsDegraded);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task AgentUnavailableReturnsUnavailableStateWithoutInventingValue()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            EnsureConnectedResult = false
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var state = await client.GetStateAsync();

        Assert.IsFalse(state.IsAvailable);
        Assert.IsFalse(state.IsEnabled);
        Assert.AreEqual(0, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task SuccessfulMutationSendsDesiredBooleanOnce()
    {
        var connection = new FakeWindowsAgentControlConnection();
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var result = await client.SetEnabledAsync(true);

        Assert.AreEqual(true, connection.LastRequestedEnabled);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.IsFalse(result.WasOutcomeUncertain);
        Assert.IsTrue(result.State.IsEnabled);
    }


    [TestMethod]
    public async Task CancellationBeforeTransmissionPropagatesWithoutReadBackOrReplay()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FakeWindowsAgentControlConnection
        {
            CancelDuringSet = cancellation,
            SetBackgroundFailure = new WindowsAgentControlWriteException(
                WindowsAgentControlWriteTransmissionState.DefinitelyNotSent,
                new OperationCanceledException(cancellation.Token))
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.SetEnabledAsync(true, cancellation.Token));

        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(0, connection.DisconnectCount);
        Assert.AreEqual(0, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task CancellationAfterTransmissionReadsBackAuthoritativeStateWithoutReplay()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FakeWindowsAgentControlConnection
        {
            CancelDuringSet = cancellation,
            MutateBeforeSetFailure = true,
            SetBackgroundFailure = new WindowsAgentControlWriteException(
                WindowsAgentControlWriteTransmissionState.Sent,
                new OperationCanceledException(cancellation.Token))
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var result = await client.SetEnabledAsync(true, cancellation.Token);

        Assert.IsTrue(result.WasOutcomeUncertain);
        Assert.IsTrue(result.State.IsEnabled);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task CancellationWithUnknownTransmissionReadsBackWithoutReplay()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FakeWindowsAgentControlConnection
        {
            CancelDuringSet = cancellation,
            MutateBeforeSetFailure = true,
            SetBackgroundFailure = new WindowsAgentControlWriteException(
                WindowsAgentControlWriteTransmissionState.TransmissionUnknown,
                new OperationCanceledException(cancellation.Token))
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var result = await client.SetEnabledAsync(true, cancellation.Token);

        Assert.IsTrue(result.WasOutcomeUncertain);
        Assert.IsTrue(result.State.IsEnabled);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task ReadBackReconnectsAgainAfterInitialConnectionLossWithoutReplay()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            MutateBeforeSetFailure = true,
            SetBackgroundFailure = new WindowsAgentControlWriteException(
                WindowsAgentControlWriteTransmissionState.Sent,
                new IOException("response lost"))
        };
        connection.EnsureConnectedResults.Enqueue(true);
        connection.EnsureConnectedResults.Enqueue(false);
        connection.EnsureConnectedResults.Enqueue(true);
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var result = await client.SetEnabledAsync(true);

        Assert.IsTrue(result.WasOutcomeUncertain);
        Assert.IsTrue(result.State.IsEnabled);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(2, connection.DisconnectCount);
        Assert.AreEqual(3, connection.EnsureConnectedCount);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }

    [TestMethod]
    public async Task LostMutationResponseReconnectsAndReadsBackWithoutReplay()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            MutateBeforeSetFailure = true,
            SetBackgroundFailure = new IOException("response lost")
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(connection);

        var result = await client.SetEnabledAsync(true);

        Assert.IsTrue(result.WasOutcomeUncertain);
        Assert.IsTrue(result.State.IsEnabled);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(1, connection.DisconnectCount);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }
    [TestMethod]
    [Timeout(5_000)]
    public async Task SilentAuthoritativeReadBackHasIndependentBoundedDeadline()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            MutateBeforeSetFailure = true,
            SetBackgroundFailure = new WindowsAgentControlWriteException(
                WindowsAgentControlWriteTransmissionState.Sent,
                new IOException("response lost")),
            StallBackgroundRead = true
        };
        var client = new WindowsAgentBackgroundSyncSettingsClient(
            connection,
            TimeSpan.FromMilliseconds(100));

        var result = await client.SetEnabledAsync(true);

        Assert.IsTrue(result.WasOutcomeUncertain);
        Assert.IsFalse(result.State.IsAvailable);
        Assert.AreEqual(1, connection.SetBackgroundCount);
        Assert.AreEqual(1, connection.GetBackgroundCount);
    }

    [TestMethod]
    public void NonPositiveReadBackTimeoutIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsAgentBackgroundSyncSettingsClient(
                new FakeWindowsAgentControlConnection(),
                TimeSpan.Zero));
    }

}
