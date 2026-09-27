using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Validation;

public sealed class WindowsIpcContractValidator
{
    public void ValidateForTransport<T>(T value)
    {
        if (value is null)
            throw new IpcPayloadException("The IPC transport value cannot be null.");

        switch (value)
        {
            case IpcHandshakeRequest item:
                Validate(item);
                break;
            case IpcHandshakeResponse item:
                Validate(item);
                break;
            case IpcRequestEnvelope item:
                Validate(item);
                break;
            case IpcResponseEnvelope item:
                Validate(item);
                break;
            case IpcError item:
                Validate(item);
                break;
            case IpcFailureDto item:
                Validate(item);
                break;
            case AgentStatusDto item:
                Validate(item);
                break;
            case BackendRuntimeStatusDto item:
                Validate(item);
                break;
            case InteractiveSessionStatusDto item:
                Validate(item);
                break;
            case SynchronizationStatusDto item:
                Validate(item);
                break;
            case WindowsBackgroundSyncStateDto item:
                Validate(item);
                break;
            case PingResponseDto item:
                ValidateTimestamp(item.ServerTimeUtc, "The IPC server timestamp is invalid.");
                break;
            case UiActivationRequestDto item:
                Validate(item);
                break;
            case UiOpenRequestDto item:
                Validate(item);
                break;
            case AgentExitRequestDto item:
                Validate(item);
                break;
            case SetBackgroundSyncEnabledRequestDto:
            case RequestAcceptedDto:
            case UiConnectionRegistrationResponseDto:
                break;
            case DatabaseResetResultDto item:
                Validate(item);
                break;
            default:
                throw new IpcPayloadException(
                    "The IPC transport type does not have semantic validation.");
        }
    }

    public void Validate(IpcHandshakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion <= 0 ||
            !Enum.IsDefined(request.ClientRole) ||
            request.ProcessId <= 0 ||
            request.WindowsSessionId < 0 ||
            request.SessionId == Guid.Empty ||
            request.Capabilities == IpcCapabilities.None ||
            HasUnknownCapabilities(request.Capabilities))
        {
            throw new IpcPayloadException("The IPC handshake request is semantically invalid.");
        }
    }

    public void Validate(IpcHandshakeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion <= 0 ||
            !Enum.IsDefined(response.ServerRole) ||
            response.ServerRole == IpcPeerRole.TestClient ||
            response.ConnectionId == Guid.Empty ||
            response.Capabilities == IpcCapabilities.None ||
            HasUnknownCapabilities(response.Capabilities) ||
            response.Accepted == (response.Error is not null))
        {
            throw new IpcPayloadException("The IPC handshake response is semantically invalid.");
        }

        if (response.Error is not null)
            Validate(response.Error);
    }

    public void Validate(IpcRequestEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CorrelationId <= 0 || !Enum.IsDefined(request.OperationId))
            throw new IpcPayloadException("The IPC request envelope is semantically invalid.");

        if (request.Payload?.Length > IpcContractLimits.MaximumInnerPayloadSize)
        {
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.RequestPayloadTooLarge,
                "The IPC request payload exceeds the permitted size.",
                request.CorrelationId);
        }
    }

    public void Validate(IpcResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.CorrelationId <= 0 ||
            response.IsSuccess == (response.Error is not null) ||
            (!response.IsSuccess && response.Result is not null))
        {
            throw new IpcPayloadException("The IPC response envelope is semantically invalid.");
        }

        if (response.Result?.Length > IpcContractLimits.MaximumInnerPayloadSize)
        {
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.ResponsePayloadTooLarge,
                "The IPC response result exceeds the permitted size.",
                response.CorrelationId);
        }

        if (response.Error is not null)
        {
            Validate(response.Error);
            if (response.Error.CorrelationId != response.CorrelationId)
                throw new IpcPayloadException("The IPC response error correlation ID is invalid.");
        }
    }

    public void ValidateSerializedRequestEnvelope(
        long correlationId,
        ReadOnlySpan<byte> serializedEnvelope)
    {
        ValidateSerializedEnvelope(
            correlationId,
            serializedEnvelope,
            "The serialized IPC request envelope exceeds the permitted frame size.");
    }

    public void ValidateSerializedResponseEnvelope(
        long correlationId,
        ReadOnlySpan<byte> serializedEnvelope)
    {
        ValidateSerializedEnvelope(
            correlationId,
            serializedEnvelope,
            "The serialized IPC response envelope exceeds the permitted frame size.");
    }

    public void Validate(IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Enum.IsDefined(error.ErrorCode) ||
            !Enum.IsDefined(error.ErrorCategory) ||
            error.CorrelationId <= 0)
        {
            throw new IpcPayloadException("The IPC error metadata is invalid.");
        }

        ValidateSafeMessage(error.SafeMessage);
        ValidateTimestamp(error.OccurredAtUtc, "The IPC error timestamp is invalid.");

        if (error.RequiresProcessRestart &&
            (error.ErrorCategory is IpcErrorCategory.Protocol or
                IpcErrorCategory.Validation or
                IpcErrorCategory.Cancellation or
                IpcErrorCategory.Conflict))
        {
            throw new IpcPayloadException("The IPC error restart requirement is invalid.");
        }

        if ((error.ErrorCode is IpcErrorCode.ServerBusy or
                IpcErrorCode.TooManyRequests or
                IpcErrorCode.TooManyConnections) &&
            (error.ErrorCategory != IpcErrorCategory.Availability ||
                !error.IsRetryable ||
                error.RequiresProcessRestart))
        {
            throw new IpcPayloadException("The IPC busy error metadata is invalid.");
        }

        if (error.ErrorCode == IpcErrorCode.RequestCancelled &&
            error.ErrorCategory != IpcErrorCategory.Cancellation)
        {
            throw new IpcPayloadException("The IPC cancellation error category is invalid.");
        }

        if ((error.ErrorCode is IpcErrorCode.RequestPayloadTooLarge or
                IpcErrorCode.ResponsePayloadTooLarge or
                IpcErrorCode.SerializedEnvelopeTooLarge) &&
            (error.ErrorCategory != IpcErrorCategory.Validation ||
                error.IsRetryable ||
                error.RequiresProcessRestart))
        {
            throw new IpcPayloadException("The IPC payload-limit error metadata is invalid.");
        }
    }

    public void Validate(IpcFailureDto failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(failure.FailureKind) ||
            failure.FailureKind == IpcFailureKind.None)
        {
            throw new IpcPayloadException("The IPC failure kind is invalid.");
        }

        ValidateSafeMessage(failure.SafeMessage);
        ValidateTimestamp(failure.OccurredAtUtc, "The IPC failure timestamp is invalid.");
        if (failure.RequiresProcessRestart &&
            failure.FailureKind == IpcFailureKind.Synchronization)
        {
            throw new IpcPayloadException("The IPC failure restart requirement is invalid.");
        }
    }

    public void Validate(AgentStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.AgentState))
            throw new IpcPayloadException("The IPC agent state is invalid.");
        if (!Enum.IsDefined(status.AdmissionState))
            throw new IpcPayloadException("The IPC agent admission state is invalid.");

        if (status.AdmissionState == AgentAdmissionState.Open &&
            status.AgentState != AgentState.Running)
        {
            throw new IpcPayloadException("Open admission requires a running agent.");
        }

        if (status.StartedAtUtc.HasValue)
            ValidateTimestamp(status.StartedAtUtc.Value, "The IPC agent start timestamp is invalid.");

        if ((status.AgentState is AgentState.NotStarted or AgentState.Stopped) &&
            status.StartedAtUtc is not null)
        {
            throw new IpcPayloadException("The IPC agent start timestamp is inconsistent with its state.");
        }

        if (status.AgentState == AgentState.Running &&
            status.StartedAtUtc is null)
        {
            throw new IpcPayloadException("The IPC running agent has no start timestamp.");
        }

        if (status.IsBackendRunning && !status.BackendOwnedByAgent)
            throw new IpcPayloadException("The IPC backend-running flag requires agent ownership.");

        if ((status.IsEndpointHostReady || status.IsDatabaseResetInProgress ||
                status.HasInteractiveUiLease || status.HasBackgroundSyncLease) &&
            !status.BackendOwnedByAgent)
        {
            throw new IpcPayloadException("Agent runtime details require agent backend ownership.");
        }

        if (status.IsEndpointHostReady && status.AdmissionState != AgentAdmissionState.Open)
            throw new IpcPayloadException("Endpoint readiness requires open agent admission.");

        if (status.IsEndpointHostReady && status.IsDatabaseResetInProgress)
            throw new IpcPayloadException("The endpoint host cannot be ready during database reset.");

        if (status.BackendOwnedByAgent &&
            (status.AgentState is not AgentState.Running and
                not AgentState.Stopping and
                not AgentState.Failed))
        {
            throw new IpcPayloadException("The IPC backend-ownership flag is inconsistent with the agent state.");
        }

        if (status.IsUiConnected &&
            (status.AgentState is AgentState.NotStarted or AgentState.Stopped))
        {
            throw new IpcPayloadException("The IPC UI-connected flag is inconsistent with the agent state.");
        }

        if (status.AgentState == AgentState.Failed && status.LastFailure is null)
            throw new IpcPayloadException("The IPC failed agent has no failure details.");

        if (status.LastFailure is not null &&
            status.AgentState is not AgentState.Failed and not AgentState.Stopping &&
            !status.RequiresProcessRestart)
        {
            throw new IpcPayloadException("The IPC agent failure is inconsistent with its state.");
        }

        if (status.LastFailure is not null)
            Validate(status.LastFailure);

        if (status.RequiresProcessRestart !=
            (status.LastFailure?.RequiresProcessRestart ?? false))
        {
            throw new IpcPayloadException("The IPC agent restart requirement is inconsistent.");
        }
    }

    public void Validate(BackendRuntimeStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.RuntimeState) ||
            !Enum.IsDefined(status.FailureKind))
        {
            throw new IpcPayloadException("The IPC backend runtime status contains an invalid enum value.");
        }

        ValidateTimestamp(status.ChangedAtUtc, "The IPC backend runtime timestamp is invalid.");
        var hasFailure = status.Failure is not null;
        var expectsFailure = status.RuntimeState is
            BackendRuntimeStatusState.WaitingForDeviceUnlock or BackendRuntimeStatusState.Failed;
        if (hasFailure != expectsFailure ||
            (status.FailureKind == BackendRuntimeFailureStatusKind.None) == hasFailure)
        {
            throw new IpcPayloadException("The IPC backend failure is inconsistent with its state.");
        }

        if (status.RuntimeState == BackendRuntimeStatusState.WaitingForDeviceUnlock &&
            status.FailureKind != BackendRuntimeFailureStatusKind.PlatformKeyUnavailable)
        {
            throw new IpcPayloadException("The IPC device-unlock state has an invalid failure kind.");
        }

        var expectsDatabaseCompatibility =
            status.FailureKind == BackendRuntimeFailureStatusKind.DatabaseCompatibility;
        if ((status.DatabaseCompatibility is not null) != expectsDatabaseCompatibility)
        {
            throw new IpcPayloadException(
                "The IPC database compatibility status is inconsistent with the backend failure kind.");
        }

        if (status.DatabaseCompatibility is not null)
        {
            var compatibility = status.DatabaseCompatibility;
            if (compatibility.OldestSupportedVersion < 0 ||
                compatibility.CurrentVersion < compatibility.OldestSupportedVersion ||
                compatibility.DetectedVersion < 0)
            {
                throw new IpcPayloadException(
                    "The IPC database compatibility version range is invalid.");
            }
        }

        if (status.Failure is not null)
        {
            Validate(status.Failure);
            if (status.Failure.FailureKind != MapFailureKind(status.FailureKind))
                throw new IpcPayloadException("The IPC backend failure kinds are inconsistent.");
        }

        if (status.RequiresProcessRestart !=
            (status.Failure?.RequiresProcessRestart ?? false))
        {
            throw new IpcPayloadException("The IPC backend restart requirement is inconsistent.");
        }
    }

    public void Validate(InteractiveSessionStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.LifecycleState) || status.ActiveOperationCount < 0)
            throw new IpcPayloadException("The IPC interactive-session status is invalid.");

        ValidateTimestamp(status.ChangedAtUtc, "The IPC interactive-session timestamp is invalid.");
        if (status.AcceptsNewOperations !=
            (status.LifecycleState == InteractiveSessionStatusState.Active))
        {
            throw new IpcPayloadException("The IPC interactive-session acceptance flag is invalid.");
        }

        if ((status.LifecycleState is InteractiveSessionStatusState.None or
                InteractiveSessionStatusState.Opening or
                InteractiveSessionStatusState.CleanupFailed) &&
            status.ActiveOperationCount != 0)
        {
            throw new IpcPayloadException("The IPC interactive-session operation count is invalid.");
        }

        if ((status.LifecycleState == InteractiveSessionStatusState.CleanupFailed) !=
            (status.CleanupFailure is not null))
        {
            throw new IpcPayloadException("The IPC interactive cleanup failure is inconsistent with its state.");
        }

        if (status.CleanupFailure is not null)
        {
            Validate(status.CleanupFailure);
            if (status.CleanupFailure.FailureKind != IpcFailureKind.InteractiveCleanup)
                throw new IpcPayloadException("The IPC interactive cleanup failure kind is invalid.");
        }
    }

    public void Validate(SynchronizationStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.State))
            throw new IpcPayloadException("The IPC synchronization state is invalid.");

        if ((status.State == SynchronizationStatusState.Degraded) !=
            (status.LastFailure is not null))
        {
            throw new IpcPayloadException("The IPC synchronization failure is inconsistent with its state.");
        }

        if (status.LastFailure is not null)
        {
            Validate(status.LastFailure);
            if (status.LastFailure.FailureKind != IpcFailureKind.Synchronization)
                throw new IpcPayloadException("The IPC synchronization failure kind is invalid.");
        }
    }


    public void Validate(WindowsBackgroundSyncStateDto state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Consistency) || !Enum.IsDefined(state.FailureKind))
            throw new IpcPayloadException("The background synchronization consistency is invalid.");

        if (state.Consistency == WindowsBackgroundSyncConsistency.Operational &&
            (!state.IsEnabled || !state.IsStartupRegistered ||
                !state.IsBackgroundLeaseActive || !state.IsRuntimeRunning ||
                state.IsTransitionInProgress || state.Failure is not null))
        {
            throw new IpcPayloadException("The operational background synchronization state is inconsistent.");
        }

        if (state.Consistency == WindowsBackgroundSyncConsistency.Disabled &&
            (state.IsEnabled || state.IsStartupRegistered ||
                state.IsBackgroundLeaseActive || state.IsTransitionInProgress ||
                state.Failure is not null))
        {
            throw new IpcPayloadException("The disabled background synchronization state is inconsistent.");
        }

        if ((state.Consistency == WindowsBackgroundSyncConsistency.Transitioning) !=
            state.IsTransitionInProgress)
        {
            throw new IpcPayloadException("The background synchronization transition state is inconsistent.");
        }

        var requiresFailure = state.Consistency is
            WindowsBackgroundSyncConsistency.Degraded or
            WindowsBackgroundSyncConsistency.Inconsistent or
            WindowsBackgroundSyncConsistency.Unavailable;
        if (requiresFailure != (state.Failure is not null) ||
            requiresFailure != (state.FailureKind != WindowsBackgroundSyncFailureKind.None))
        {
            throw new IpcPayloadException("The background synchronization failure is inconsistent.");
        }

        if (state.Failure is not null)
        {
            Validate(state.Failure);
            if (state.Failure.FailureKind != IpcFailureKind.BackgroundConfiguration)
                throw new IpcPayloadException("The background synchronization failure kind is invalid.");
        }
    }

    public void Validate(DatabaseResetResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Completed && result.RequiresProcessRestart)
            throw new IpcPayloadException("A completed database reset cannot require process restart.");
        if (result.Completed && result.SafeMessage is not null)
            throw new IpcPayloadException("A completed database reset cannot contain a failure message.");
        if (!result.Completed && string.IsNullOrWhiteSpace(result.SafeMessage))
            throw new IpcPayloadException("A failed database reset must contain a safe message.");
        if (result.SafeMessage is { Length: > IpcContractLimits.MaximumSafeMessageLength })
            throw new IpcPayloadException("The database reset message is too long.");
    }

    public void Validate(UiActivationRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason) || !Enum.IsDefined(request.Command))
            throw new IpcPayloadException("The IPC UI activation request is invalid.");
        if (request.Command == UiActivationCommand.IntentionalAgentShutdown &&
            (request.BringToForeground || request.Reason != UiActivationReason.AgentRequest))
        {
            throw new IpcPayloadException(
                "A UI shutdown request must use the narrow local agent-shutdown contract.");
        }
    }

    public void Validate(UiOpenRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason))
            throw new IpcPayloadException("The IPC UI open reason is invalid.");
    }

    public void Validate(AgentExitRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason))
            throw new IpcPayloadException("The IPC agent exit reason is invalid.");
    }

    private void ValidateSerializedEnvelope(
        long correlationId,
        ReadOnlySpan<byte> serializedEnvelope,
        string safeMessage)
    {
        if (correlationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        if (serializedEnvelope.Length > WindowsIpcProtocol.MaximumPayloadSize)
        {
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.SerializedEnvelopeTooLarge,
                safeMessage,
                correlationId);
        }
    }

    private static IpcFailureKind MapFailureKind(BackendRuntimeFailureStatusKind failureKind) =>
        failureKind switch
        {
            BackendRuntimeFailureStatusKind.DatabaseCompatibility => IpcFailureKind.Runtime,
            BackendRuntimeFailureStatusKind.PlatformKeyUnavailable => IpcFailureKind.PlatformKey,
            BackendRuntimeFailureStatusKind.StorageUnavailable => IpcFailureKind.Storage,
            BackendRuntimeFailureStatusKind.StartupFailure => IpcFailureKind.Runtime,
            BackendRuntimeFailureStatusKind.InteractiveCleanupFailure => IpcFailureKind.InteractiveCleanup,
            BackendRuntimeFailureStatusKind.ShutdownFailure => IpcFailureKind.Runtime,
            _ => IpcFailureKind.None
        };

    private static void ValidateSafeMessage(string safeMessage)
    {
        if (string.IsNullOrWhiteSpace(safeMessage) ||
            safeMessage.Length > IpcContractLimits.MaximumSafeMessageLength)
        {
            throw new IpcPayloadException("The IPC safe message is invalid.");
        }
    }

    private static void ValidateTimestamp(DateTimeOffset timestamp, string errorMessage)
    {
        if (timestamp == default || timestamp.Offset != TimeSpan.Zero)
            throw new IpcPayloadException(errorMessage);
    }

    private static bool HasUnknownCapabilities(IpcCapabilities capabilities)
    {
        const IpcCapabilities known =
            IpcCapabilities.Control |
            IpcCapabilities.UiActivation |
            IpcCapabilities.Status |
            IpcCapabilities.EndpointRpc;
        return (capabilities & ~known) != 0;
    }
}
