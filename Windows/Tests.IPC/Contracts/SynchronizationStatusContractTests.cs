using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Contracts;

[TestClass]
public sealed class SynchronizationStatusContractTests
{
    [TestMethod]
    public void EveryBackendRuntimeStateHasMatchingTransportStateAndUnavailableIsTransportOnly()
    {
        var backendStates = Enum.GetNames<SyncRuntimeState>();
        var transportStates = Enum.GetNames<SynchronizationStatusState>();

        foreach (var backendState in backendStates)
        {
            Assert.IsTrue(transportStates.Contains(
                backendState,
                StringComparer.Ordinal));
        }

        CollectionAssert.AreEquivalent(
            new[] { nameof(SynchronizationStatusState.Unavailable) },
            transportStates.Except(backendStates).ToArray());

        foreach (var backendState in Enum.GetValues<SyncRuntimeState>())
        {
            var transportState = Enum.Parse<SynchronizationStatusState>(
                backendState.ToString());
            Assert.AreEqual((int)backendState, (int)transportState);
        }
    }

    [TestMethod]
    [DataRow(SynchronizationStatusState.Disabled)]
    [DataRow(SynchronizationStatusState.Starting)]
    [DataRow(SynchronizationStatusState.Running)]
    [DataRow(SynchronizationStatusState.Stopping)]
    [DataRow(SynchronizationStatusState.Degraded)]
    public void RuntimeStatesRoundTripWithRequiredFailureMetadata(SynchronizationStatusState state)
    {
        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();
        var original = new SynchronizationStatusDto(state,
            state == SynchronizationStatusState.Degraded
                ? new IpcFailureDto(IpcFailureKind.Synchronization, "Synchronization is degraded.",
                    DateTimeOffset.UtcNow, IsRetryable: true, RequiresProcessRestart: false)
                : null);
        validator.Validate(original);

        var payload = serializer.Serialize(original, WindowsIpcJsonContext.Default.SynchronizationStatusDto);
        var restored = serializer.Deserialize(payload, WindowsIpcJsonContext.Default.SynchronizationStatusDto);

        validator.Validate(restored);
        Assert.AreEqual(original, restored);
    }

    [TestMethod]
    public void SynchronizationStatusExposesOnlyStateAndLastFailure()
    {
        var propertyNames = typeof(SynchronizationStatusDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "State", "LastFailure" },
            propertyNames);
    }
}
