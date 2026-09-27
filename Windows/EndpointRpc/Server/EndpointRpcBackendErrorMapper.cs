using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointRpcBackendErrorMapper
{
    public EndpointRpcError Map(Exception exception, EndpointRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        if (exception is MutationPartiallyCommittedException partialCommitException)
            return MapPartialCommit(partialCommitException, context);

        var requiresProcessRestart = ExceptionRestartRequirementClassifier.RequiresProcessRestart(exception);
        var metadata = exception switch
        {
            _ when requiresProcessRestart =>
                Error(EndpointRpcErrorCode.RuntimeUnavailable, EndpointRpcErrorCategory.Availability, "The backend runtime requires process replacement.", false, true),
            ArgumentException =>
                Error(EndpointRpcErrorCode.ValidationFailed, EndpointRpcErrorCategory.Validation, "The endpoint input is invalid."),
            SyncRouteDisabledException =>
                Error(EndpointRpcErrorCode.OperationRejected, EndpointRpcErrorCategory.Validation, "The endpoint operation was rejected."),
            UserNotFoundException or UsernameChangedDuringLoginException
                when context.OperationId == EndpointOperationId.Login =>
                Error(EndpointRpcErrorCode.AuthenticationFailed, EndpointRpcErrorCategory.Authentication, "Authentication failed."),
            UnauthorizedAccessException when context.OperationId == EndpointOperationId.Login =>
                Error(EndpointRpcErrorCode.AuthenticationFailed, EndpointRpcErrorCategory.Authentication, "Authentication failed."),
            InvalidTokenException =>
                Error(EndpointRpcErrorCode.AuthorizationFailed, EndpointRpcErrorCategory.Authorization, "The endpoint authorization is invalid."),
            UnauthorizedAccessException =>
                Error(EndpointRpcErrorCode.AuthorizationFailed, EndpointRpcErrorCategory.Authorization, "The endpoint operation is not authorized."),
            UserNotFoundException or PasswordNotFoundException or PasswordTagNotFoundException or CustomUserColorNotFoundException =>
                Error(EndpointRpcErrorCode.NotFound, EndpointRpcErrorCategory.NotFound, "The requested endpoint resource was not found."),
            DuplicatePasswordNameException or DuplicatePasswordTagNameException or
                DuplicateCustomUserColorNameException or DuplicateCustomUserColorCodeException or
                DeterministicSyncConflictException or RecoveryEvidenceConflictException =>
                Error(EndpointRpcErrorCode.Conflict, EndpointRpcErrorCategory.Conflict, "The endpoint operation conflicts with current state."),
            LimitReachedException =>
                Error(EndpointRpcErrorCode.OperationRejected, EndpointRpcErrorCategory.Validation, "The endpoint operation was rejected."),
            DeviceEnrollmentException enrollmentException =>
                MapDeviceEnrollmentError(enrollmentException.ErrorCode),
            DeviceIdentityNotInitilaizedException =>
                Error(EndpointRpcErrorCode.RuntimeUnavailable, EndpointRpcErrorCategory.Availability, "The backend runtime is temporarily unavailable.", true),
            KeyProtectorUnavailableException =>
                Error(EndpointRpcErrorCode.RuntimeUnavailable, EndpointRpcErrorCategory.Availability, "The backend runtime is temporarily unavailable.", true),
            CryptographicException or InvalidDataIntegrityException =>
                Error(EndpointRpcErrorCode.BackendFailure, EndpointRpcErrorCategory.Internal, "The backend could not safely complete the operation."),
            InvalidOperationException =>
                Error(EndpointRpcErrorCode.InteractiveSessionUnavailable, EndpointRpcErrorCategory.Availability, "The interactive backend session is unavailable."),
            _ => Error(EndpointRpcErrorCode.BackendFailure, EndpointRpcErrorCategory.Internal, "The endpoint operation failed.")
        };

        return CreateError(
            metadata,
            context,
            GetConclusiveMutationOutcome(context.OperationId),
            recovery: null);
    }

    public bool TryMapKnownMutationFailure(
        Exception exception,
        EndpointRequestContext context,
        out EndpointRpcError error)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        if (exception is MutationPartiallyCommittedException partialCommitException &&
            EndpointOperationManifest.Get(context.OperationId).CanPartiallyCommit)
        {
            error = MapPartialCommit(partialCommitException, context);
            return true;
        }

        var isConclusive = exception switch
        {
            InvalidInputException => true,
            UserNotFoundException or UsernameChangedDuringLoginException
                when context.OperationId == EndpointOperationId.Login => true,
            UnauthorizedAccessException when context.OperationId == EndpointOperationId.Login => true,
            InvalidTokenException => true,
            UserNotFoundException or PasswordNotFoundException or PasswordTagNotFoundException or
                CustomUserColorNotFoundException => true,
            DuplicatePasswordNameException or DuplicatePasswordTagNameException or
                DuplicateCustomUserColorNameException or DuplicateCustomUserColorCodeException => true,
            LimitReachedException => true,
            DeviceEnrollmentException enrollmentException when context.OperationId == EndpointOperationId.AddDeviceByCode =>
                enrollmentException.IsKnownNotCommitted,
            _ => false
        };

        if (isConclusive)
        {
            error = Map(exception, context);
            return true;
        }

        error = null!;
        return false;
    }

    public EndpointRpcError CreateOutcomeUnknown(
        EndpointRequestContext context,
        bool requiresProcessRestart = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new EndpointRpcError(
            EndpointRpcErrorCode.OperationOutcomeUnknown,
            EndpointRpcErrorCategory.Internal,
            "The endpoint operation may have executed, but its outcome could not be confirmed.",
            context.CorrelationId,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: requiresProcessRestart,
            EndpointMutationOutcome.OutcomeUnknown,
            Recovery: null);
    }

    public bool RequiresProcessRestart(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ExceptionRestartRequirementClassifier.RequiresProcessRestart(exception);
    }

    private EndpointRpcError MapPartialCommit(
        MutationPartiallyCommittedException exception,
        EndpointRequestContext context)
    {
        var descriptor = EndpointOperationManifest.Get(context.OperationId);
        var requiresProcessRestart = ExceptionRestartRequirementClassifier.RequiresProcessRestart(exception);
        if (!descriptor.CanPartiallyCommit)
            return CreateOutcomeUnknown(context, requiresProcessRestart);

        EndpointRecoveryMetadata? recovery = null;
        var safeMessage = "The endpoint operation committed authoritative state, but required follow-up work did not complete.";
        if (context.OperationId == EndpointOperationId.AddDeviceByCode)
        {
            if (exception is not DeviceEnrollmentPartiallyCommittedException enrollmentException)
                return CreateOutcomeUnknown(context, requiresProcessRestart);

            recovery = new EndpointRecoveryMetadata(
                EndpointRecoveryKind.DeviceEnrollment,
                enrollmentException.TargetDeviceId,
                enrollmentException.TargetOriginInstanceId,
                enrollmentException.EnrollmentCommitId,
                enrollmentException.RecoveryAvailable,
                enrollmentException.TransferPending,
                enrollmentException.RequiresSignedRemovalToUndo);
            safeMessage = "The device addition was committed, but enrollment recovery is required.";
        }
        else if (exception is DeviceEnrollmentPartiallyCommittedException)
        {
            return CreateOutcomeUnknown(context, requiresProcessRestart);
        }

        return new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointRpcErrorCategory.Recovery,
            safeMessage,
            context.CorrelationId,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: requiresProcessRestart,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            recovery);
    }

    private static EndpointMutationOutcome GetConclusiveMutationOutcome(EndpointOperationId operationId) =>
        EndpointOperationManifest.Get(operationId).MutatesState
            ? EndpointMutationOutcome.NotCommitted
            : EndpointMutationOutcome.NotApplicable;

    private static EndpointRpcError CreateError(
        (EndpointRpcErrorCode Code, EndpointRpcErrorCategory Category, string Message, bool Retryable, bool RequiresRestart) metadata,
        EndpointRequestContext context,
        EndpointMutationOutcome mutationOutcome,
        EndpointRecoveryMetadata? recovery) =>
        new(
            metadata.Code,
            metadata.Category,
            metadata.Message,
            context.CorrelationId,
            DateTimeOffset.UtcNow,
            metadata.Retryable,
            metadata.RequiresRestart,
            mutationOutcome,
            recovery);

    private static (EndpointRpcErrorCode Code, EndpointRpcErrorCategory Category, string Message, bool Retryable, bool RequiresRestart) MapDeviceEnrollmentError(
        DeviceEnrollmentErrorCode errorCode) => errorCode switch
    {
        DeviceEnrollmentErrorCode.Unknown =>
            Error(EndpointRpcErrorCode.BackendFailure, EndpointRpcErrorCategory.Internal, "The backend could not safely complete the enrollment operation."),
        DeviceEnrollmentErrorCode.NewDeviceNotFound =>
            Error(EndpointRpcErrorCode.NotFound, EndpointRpcErrorCategory.NotFound, "The requested endpoint resource was not found."),
        DeviceEnrollmentErrorCode.DeviceIdentityConflict =>
            Error(EndpointRpcErrorCode.Conflict, EndpointRpcErrorCategory.Conflict, "The endpoint operation conflicts with current state."),
        DeviceEnrollmentErrorCode.NewDeviceConnectionFailed or
            DeviceEnrollmentErrorCode.LocalNetworkUnavailable or
            DeviceEnrollmentErrorCode.LocalEnrollmentListenerUnavailable =>
            Error(EndpointRpcErrorCode.RuntimeUnavailable, EndpointRpcErrorCategory.Availability, "The backend runtime is temporarily unavailable.", true),
        _ =>
            Error(EndpointRpcErrorCode.OperationRejected, EndpointRpcErrorCategory.Validation, "The endpoint operation was rejected.")
    };

    private static (EndpointRpcErrorCode Code, EndpointRpcErrorCategory Category, string Message, bool Retryable, bool RequiresRestart) Error(
        EndpointRpcErrorCode code,
        EndpointRpcErrorCategory category,
        string message,
        bool retryable = false,
        bool requiresRestart = false) =>
        (code, category, message, retryable, requiresRestart);
}
