using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Validation;

[TestClass]
public sealed class WindowsIpcContractValidatorTests
{
    private readonly WindowsIpcContractValidator _validator = new();

    [TestMethod]
    public void UndefinedStatusEnumsAreRejected()
    {
        AssertInvalid(new AgentStatusDto(
            (AgentState)999,
            AgentAdmissionState.Closed,
            false,
            false,
            false,
            false,
            false,
            null,
            null));
        AssertInvalid(new AgentStatusDto(
            AgentState.NotStarted,
            (AgentAdmissionState)999,
            false,
            false,
            false,
            false,
            false,
            null,
            null));
        AssertInvalid(new BackendRuntimeStatusDto(
            (BackendRuntimeStatusState)999,
            BackendRuntimeFailureStatusKind.None,
            null,
            false,
            DateTimeOffset.UtcNow));
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Ready,
            (BackendRuntimeFailureStatusKind)999,
            null,
            false,
            DateTimeOffset.UtcNow));
        AssertInvalid(new InteractiveSessionStatusDto(
            (InteractiveSessionStatusState)999,
            false,
            0,
            null,
            DateTimeOffset.UtcNow));
        AssertInvalid(new SynchronizationStatusDto(
            (SynchronizationStatusState)999,
            null));
    }

    [TestMethod]
    public void AgentTimestampAndFailureMetadataMustMatchState()
    {
        AssertInvalid(new AgentStatusDto(
            AgentState.NotStarted,
            AgentAdmissionState.Closed,
            false,
            false,
            false,
            false,
            false,
            null,
            DateTimeOffset.UtcNow));
        AssertInvalid(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Open,
            false,
            true,
            true,
            true,
            false,
            null,
            null));
        AssertInvalid(new IpcFailureDto(
            (IpcFailureKind)999,
            "Invalid failure kind.",
            DateTimeOffset.UtcNow,
            false,
            false));
        AssertInvalid(new IpcError(
            (IpcErrorCode)999,
            IpcErrorCategory.Internal,
            "Invalid error code.",
            1,
            DateTimeOffset.UtcNow,
            false,
            false));
        AssertInvalid(new IpcError(
            IpcErrorCode.InternalFailure,
            (IpcErrorCategory)999,
            "Invalid error category.",
            1,
            DateTimeOffset.UtcNow,
            false,
            false));
    }

    [TestMethod]
    public void InteractiveSessionImpossibleCombinationsAreRejected()
    {
        AssertInvalid(new InteractiveSessionStatusDto(
            InteractiveSessionStatusState.Active,
            true,
            -1,
            null,
            DateTimeOffset.UtcNow));
        AssertInvalid(new InteractiveSessionStatusDto(
            InteractiveSessionStatusState.Closing,
            true,
            1,
            null,
            DateTimeOffset.UtcNow));
        AssertInvalid(new InteractiveSessionStatusDto(
            InteractiveSessionStatusState.Active,
            true,
            0,
            Failure(IpcFailureKind.InteractiveCleanup),
            DateTimeOffset.UtcNow));
        AssertInvalid(new InteractiveSessionStatusDto(
            InteractiveSessionStatusState.CleanupFailed,
            false,
            0,
            null,
            DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void EndpointReadinessRequiresOpenAdmission()
    {
        AssertInvalid(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Closed,
            false,
            true,
            true,
            false,
            false,
            null,
            DateTimeOffset.UtcNow,
            IsEndpointHostReady: true));
    }

    [TestMethod]
    public void FailureAndStateMismatchesAreRejected()
    {
        AssertInvalid(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Open,
            false,
            true,
            true,
            false,
            false,
            Failure(IpcFailureKind.Runtime),
            DateTimeOffset.UtcNow));
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Ready,
            BackendRuntimeFailureStatusKind.StartupFailure,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow));
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.StorageUnavailable,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow));
        AssertInvalid(new SynchronizationStatusDto(
            SynchronizationStatusState.Running,
            Failure(IpcFailureKind.Synchronization)));
        AssertInvalid(new SynchronizationStatusDto(
            SynchronizationStatusState.Degraded,
            null));
    }

    [TestMethod]
    public void InvalidRestartRequirementsAreRejected()
    {
        AssertInvalid(new IpcError(
            IpcErrorCode.InvalidPayload,
            IpcErrorCategory.Validation,
            "Invalid payload.",
            4,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: true));
        AssertInvalid(new IpcFailureDto(
            IpcFailureKind.Synchronization,
            "Synchronization failed.",
            DateTimeOffset.UtcNow,
            IsRetryable: true,
            RequiresProcessRestart: true));
        AssertInvalid(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Open,
            false,
            true,
            true,
            true,
            true,
            null,
            DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void SafeMessagesAndTimestampsAreBoundedAndRequired()
    {
        AssertInvalid(new IpcFailureDto(
            IpcFailureKind.Runtime,
            string.Empty,
            DateTimeOffset.UtcNow,
            false,
            false));
        AssertInvalid(new IpcFailureDto(
            IpcFailureKind.Runtime,
            new string('x', IpcContractLimits.MaximumSafeMessageLength + 1),
            DateTimeOffset.UtcNow,
            false,
            false));
        AssertInvalid(new IpcFailureDto(
            IpcFailureKind.Runtime,
            "Runtime failed.",
            default,
            false,
            false));
        AssertInvalid(new IpcFailureDto(
            IpcFailureKind.Runtime,
            "Runtime failed.",
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.FromHours(2)),
            false,
            false));
    }

    [TestMethod]
    public void BusyErrorsHaveStableRetryableAvailabilitySemantics()
    {
        foreach (var errorCode in new[]
        {
            IpcErrorCode.ServerBusy,
            IpcErrorCode.TooManyRequests
        })
        {
            var valid = new IpcError(
                errorCode,
                IpcErrorCategory.Availability,
                "The IPC channel is busy.",
                8,
                DateTimeOffset.UtcNow,
                IsRetryable: true,
                RequiresProcessRestart: false);
            _validator.Validate(valid);

            AssertInvalid(valid with { ErrorCategory = IpcErrorCategory.Protocol });
            AssertInvalid(valid with { IsRetryable = false });
            AssertInvalid(valid with { RequiresProcessRestart = true });
        }
    }

    [TestMethod]
    public void RequestDtosRejectUndefinedEnumsAndInvalidHandshakeIdentity()
    {
        AssertInvalid(new UiActivationRequestDto((UiActivationReason)999, true));
        AssertInvalid(new UiActivationRequestDto(
            UiActivationReason.UserLaunch,
            BringToForeground: false,
            UiActivationCommand.IntentionalAgentShutdown));
        AssertInvalid(new UiActivationRequestDto(
            UiActivationReason.AgentRequest,
            BringToForeground: true,
            UiActivationCommand.IntentionalAgentShutdown));
        AssertInvalid(new UiOpenRequestDto((UiActivationReason)999));
        AssertInvalid(new AgentExitRequestDto((AgentExitReason)999));
        AssertInvalid(new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Ui,
            0,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control));
        AssertInvalid(new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Ui,
            Environment.ProcessId,
            0,
            Guid.Empty,
            IpcCapabilities.Control));
        AssertInvalid(new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Ui,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            IpcCapabilities.None));
        AssertInvalid(new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Ui,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            (IpcCapabilities)128));
    }

    [TestMethod]
    public void DatabaseCompatibilityMetadataMustMatchFailureKindAndRange()
    {
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.DatabaseCompatibility,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow));
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.StartupFailure,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow,
            new DatabaseCompatibilityStatusDto(99, 12, 12)));
        AssertInvalid(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.DatabaseCompatibility,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow,
            new DatabaseCompatibilityStatusDto(99, 13, 12)));

        _validator.Validate(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.DatabaseCompatibility,
            Failure(IpcFailureKind.Runtime),
            false,
            DateTimeOffset.UtcNow,
            new DatabaseCompatibilityStatusDto(99, 12, 12)));
    }

    [TestMethod]
    public void ValidStatusDtosAreAccepted()
    {
        _validator.Validate(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Open,
            true,
            true,
            true,
            true,
            false,
            null,
            DateTimeOffset.UtcNow));
        _validator.Validate(new BackendRuntimeStatusDto(
            BackendRuntimeStatusState.Ready,
            BackendRuntimeFailureStatusKind.None,
            null,
            false,
            DateTimeOffset.UtcNow));
        _validator.Validate(new InteractiveSessionStatusDto(
            InteractiveSessionStatusState.Active,
            true,
            2,
            null,
            DateTimeOffset.UtcNow));
        _validator.Validate(new SynchronizationStatusDto(
            SynchronizationStatusState.Degraded,
            Failure(IpcFailureKind.Synchronization)));
        _validator.ValidateForTransport(new SetBackgroundSyncEnabledRequestDto(true));
        _validator.ValidateForTransport(new WindowsBackgroundSyncStateDto(
            true,
            true,
            true,
            true,
            false,
            WindowsBackgroundSyncConsistency.Operational,
            WindowsBackgroundSyncFailureKind.None,
            null));
        _validator.ValidateForTransport(new RequestAcceptedDto(true));
        _validator.ValidateForTransport(new UiConnectionRegistrationResponseDto(true));
    }


    [TestMethod]
    public void DegradedBackgroundStateCanReportLeaseWithoutRunningRuntime()
    {
        _validator.ValidateForTransport(new WindowsBackgroundSyncStateDto(
            IsEnabled: true,
            IsStartupRegistered: true,
            IsBackgroundLeaseActive: true,
            IsRuntimeRunning: false,
            IsTransitionInProgress: false,
            WindowsBackgroundSyncConsistency.Degraded,
            WindowsBackgroundSyncFailureKind.RuntimeLease,
            Failure(IpcFailureKind.BackgroundConfiguration)));
    }

    [TestMethod]
    public async Task InvalidTypedServerStatusDoesNotReachControlClient()
    {
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.GetAgentStatus,
            (context, _) => Task.FromResult(context.Success(
                new AgentStatusDto(
                    (AgentState)999,
                    AgentAdmissionState.Closed,
                    false,
                    false,
                    false,
                    false,
                    false,
                    null,
                    null),
                WindowsIpcJsonContext.Default.AgentStatusDto)));
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });
        var controlClient = new WindowsIpcControlClient(
            session.Client,
            session.Serializer,
            _validator);

        var observed = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await controlClient.GetAgentStatusAsync());

        Assert.AreEqual(IpcProtocolErrorCode.InvalidEnvelope, observed.ErrorCode);
    }

    private void AssertInvalid<T>(T value) =>
        Assert.ThrowsExactly<IpcPayloadException>(() => _validator.ValidateForTransport(value));

    private static IpcFailureDto Failure(IpcFailureKind kind) =>
        new(
            kind,
            "A safe failure occurred.",
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: false);
}
