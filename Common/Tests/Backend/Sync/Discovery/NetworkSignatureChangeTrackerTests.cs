using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Services.Hosted;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class NetworkSignatureChangeTrackerTests
{
    [TestMethod]
    public void DuplicateNetworkEvent_WithSameConfiguration_DoesNotRequireRestart()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsFalse(tracker.Observe("True:interface-a", isNetworkEvent: true));
        Assert.IsFalse(tracker.RequiresRefresh("True:interface-a"));
        Assert.IsFalse(tracker.HasUnappliedTransition);
    }

    [TestMethod]
    public void ChangedSignature_RequiresRestartEvenIfItRevertsDuringDebounce()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsTrue(tracker.Observe("True:interface-b", isNetworkEvent: true));
        Assert.IsTrue(tracker.RequiresRefresh("True:interface-a"));

        tracker.MarkRefreshed("True:interface-a", currentAvailability: true, startedAtVersion: tracker.ObservationVersion);

        Assert.IsFalse(tracker.RequiresRefresh("True:interface-a"));
    }

    [TestMethod]
    public void ActualChange_IsRecordedByPoller_AndRetriesAfterFailedRestart()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsTrue(tracker.Observe("True:interface-b"));
        // The restart throws: MarkRefreshed is deliberately never called.
        Assert.IsTrue(tracker.HasUnappliedTransition);
        Assert.IsTrue(tracker.RequiresRefresh("True:interface-b"));

        var version = tracker.ObservationVersion;
        tracker.MarkRefreshed("True:interface-b", currentAvailability: true, startedAtVersion: version);

        Assert.IsFalse(tracker.RequiresRefresh("True:interface-b"));
        Assert.IsFalse(tracker.HasUnappliedTransition);
    }

    [TestMethod]
    public void AvailabilityLostAndRecovered_RequiresRestartWhenAddressesStayTheSame()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsTrue(tracker.Observe(
            "True:interface-a", isNetworkEvent: true, reportedAvailability: false));
        Assert.IsTrue(tracker.RequiresRefresh("True:interface-a"));
    }

    [TestMethod]
    public void RepeatedIdenticalAvailabilityEvent_DoesNotRequireRestart()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsFalse(tracker.Observe(
            "True:interface-a", isNetworkEvent: true, reportedAvailability: true));
        Assert.IsFalse(tracker.RequiresRefresh("True:interface-a"));
    }

    [TestMethod]
    public void ObservationsDuringRestart_AreNotClearedByOlderCompletion()
    {
        var tracker = new NetworkSignatureChangeTracker("True:interface-a", initialAvailability: true);

        Assert.IsTrue(tracker.Observe("True:interface-b", isNetworkEvent: true));
        var originalVersion = tracker.ObservationVersion;
        Assert.IsTrue(tracker.Observe("True:interface-c", isNetworkEvent: true));

        tracker.MarkRefreshed("True:interface-c", currentAvailability: true, startedAtVersion: originalVersion);

        Assert.IsTrue(tracker.HasUnappliedTransition);
        Assert.IsTrue(tracker.RequiresRefresh("True:interface-c"));
    }

    [TestMethod]
    public void UnknownNetworkSignature_OnOsEvent_FailsSafe()
    {
        var tracker = new NetworkSignatureChangeTracker("unknown", initialAvailability: true);

        // Polling without new evidence must not continuously restart networking.
        Assert.IsFalse(tracker.Observe("unknown"));
        Assert.IsFalse(tracker.RequiresRefresh("unknown"));

        // An OS event cannot safely be ignored if enumeration failed.
        Assert.IsTrue(tracker.Observe("unknown", isNetworkEvent: true));
        Assert.IsTrue(tracker.RequiresRefresh("unknown"));
    }
}
