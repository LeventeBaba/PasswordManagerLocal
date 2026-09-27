using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentBackendRuntimeOwnerTests
{
    [TestMethod]
    public async Task RepeatedStartCreatesExactlyOneRuntimeComposition()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var lifetime = new FakeAgentBackendLifetimeCoordinator();
        var creationCount = 0;
        await using var owner = new WindowsAgentBackendRuntimeOwner(() =>
        {
            creationCount++;
            return new BackendRuntimeComposition(runtime, lifetime, @"C:\agent-data");
        });

        await owner.StartAsync();
        await owner.StartAsync();

        Assert.AreEqual(1, creationCount);
        Assert.AreEqual(WindowsAgentBackendOwnerState.Ready, owner.Snapshot.State);
        Assert.AreEqual(BackendRuntimeState.NotStarted, owner.Snapshot.Runtime.State);
    }

    [TestMethod]
    public async Task StopAndDisposeUseTheSameRuntimeExactlyOnce()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var owner = new WindowsAgentBackendRuntimeOwner(() => new BackendRuntimeComposition(
            runtime,
            new FakeAgentBackendLifetimeCoordinator(),
            @"C:\agent-data"));
        await owner.StartAsync();

        await owner.StopAsync();
        await owner.DisposeAsync();

        Assert.AreEqual(1, runtime.StopCount);
        Assert.AreEqual(1, runtime.DisposeCount);
        Assert.AreEqual(WindowsAgentBackendOwnerState.Stopped, owner.Snapshot.State);
    }

    [TestMethod]
    public async Task RepeatedDisposeKeepsRuntimeDisposalExactlyOnce()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var owner = new WindowsAgentBackendRuntimeOwner(() => new BackendRuntimeComposition(
            runtime,
            new FakeAgentBackendLifetimeCoordinator(),
            @"C:\agent-data"));
        await owner.StartAsync();

        await owner.DisposeAsync();
        await owner.DisposeAsync();

        Assert.AreEqual(1, runtime.DisposeCount);
        Assert.AreEqual(WindowsAgentBackendOwnerState.Stopped, owner.Snapshot.State);
    }

    [TestMethod]
    public async Task RuntimeFactoryFailureCanRetryOnlyBecauseNoRuntimeWasCreated()
    {
        var creationCount = 0;
        var owner = new WindowsAgentBackendRuntimeOwner(() =>
        {
            creationCount++;
            throw new IOException("factory failed");
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => owner.StartAsync());
        await Assert.ThrowsExactlyAsync<IOException>(() => owner.StartAsync());

        Assert.AreEqual(2, creationCount);
        Assert.AreEqual(WindowsAgentBackendOwnerState.Failed, owner.Snapshot.State);
        await owner.DisposeAsync();
    }

    [TestMethod]
    public async Task RestartRequiredOwnerNeverCreatesAnotherRuntimeInSameProcess()
    {
        var creationCount = 0;
        var owner = new WindowsAgentBackendRuntimeOwner(() =>
        {
            creationCount++;
            return new BackendRuntimeComposition(
                new FakeAgentOwnedBackendRuntime(),
                new FakeAgentBackendLifetimeCoordinator(),
                @"C:\agent-data");
        });
        await owner.StartAsync();
        owner.RequireProcessRestart(new IOException("runtime contaminated"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => owner.StartAsync());

        Assert.AreEqual(1, creationCount);
        Assert.IsTrue(owner.Snapshot.RequiresProcessRestart);
        await owner.DisposeAsync();
    }


    [TestMethod]
    public async Task ReasonAcquisitionAndReleaseRefreshOwnerSnapshotWithoutRuntimeRestart()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var lifetime = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var owner = new WindowsAgentBackendRuntimeOwner(() =>
            new BackendRuntimeComposition(runtime, lifetime, @"C:\agent-data"));
        await owner.StartAsync();
        await using var interactive = await lifetime.AcquireAsync(
            BackendLifetimeReason.InteractiveUi);

        var background = await owner.AcquireBackgroundSyncLeaseAsync();

        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            owner.Snapshot.ActiveReasons);

        await background.DisposeAsync();

        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, owner.Snapshot.ActiveReasons);
        Assert.AreEqual(BackendRuntimeState.Ready, owner.Snapshot.Runtime.State);
        Assert.AreEqual(1, runtime.EnsureStartedCount);
        Assert.AreEqual(0, runtime.StopCount);
    }

    [TestMethod]
    public async Task FinalReasonReleaseRefreshesOwnerAndAllowsRuntimeStop()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var lifetime = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var owner = new WindowsAgentBackendRuntimeOwner(() =>
            new BackendRuntimeComposition(runtime, lifetime, @"C:\agent-data"));
        await owner.StartAsync();
        var background = await owner.AcquireBackgroundSyncLeaseAsync();

        await background.DisposeAsync();

        Assert.AreEqual(BackendLifetimeReason.None, owner.Snapshot.ActiveReasons);
        Assert.AreEqual(BackendRuntimeState.Stopped, owner.Snapshot.Runtime.State);
        Assert.AreEqual(1, runtime.StopCount);
    }

    [TestMethod]
    public async Task RepeatedLeaseDisposalPublishesReasonRemovalExactlyOnce()
    {
        var runtime = new FakeAgentOwnedBackendRuntime();
        var lifetime = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var owner = new WindowsAgentBackendRuntimeOwner(() =>
            new BackendRuntimeComposition(runtime, lifetime, @"C:\agent-data"));
        await owner.StartAsync();
        await using var interactive = await lifetime.AcquireAsync(
            BackendLifetimeReason.InteractiveUi);
        var background = await owner.AcquireBackgroundSyncLeaseAsync();
        var changeCount = 0;
        owner.StateChanged += (_, _) => changeCount++;

        await background.DisposeAsync();
        await background.DisposeAsync();

        Assert.AreEqual(1, changeCount);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, owner.Snapshot.ActiveReasons);
    }

    [TestMethod]
    public async Task UnsafeResetFailureRequiresAgentProcessReplacement()
    {
        var failure = new IOException("reset failed after shutdown");
        var runtime = new FakeAgentOwnedBackendRuntime
        {
            Snapshot = new BackendRuntimeSnapshot(
                BackendRuntimeState.Failed,
                BackendRuntimeFailureKind.DatabaseCompatibility,
                new InvalidOperationException("unsupported database"),
                DateTimeOffset.UtcNow),
            ResetFailure = failure
        };
        await using var owner = new WindowsAgentBackendRuntimeOwner(() => new BackendRuntimeComposition(
            runtime,
            new FakeAgentBackendLifetimeCoordinator(),
            @"C:\agent-data"));
        await owner.StartAsync();

        var observed = await Assert.ThrowsExactlyAsync<IOException>(() => owner.ResetDatabaseAsync());

        Assert.AreSame(failure, observed);
        Assert.IsTrue(owner.Snapshot.RequiresProcessRestart);
        Assert.AreEqual(WindowsAgentBackendOwnerState.RestartRequired, owner.Snapshot.State);
        Assert.AreEqual(1, runtime.ResetCount);
    }
}
