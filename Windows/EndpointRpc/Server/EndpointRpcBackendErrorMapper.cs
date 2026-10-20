using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Common.Contracts.Errors;
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
            DeviceEnrollmentException enrollmentException when requiresProcessRestart =>
                MapDeviceEnrollmentError(enrollmentException.ErrorCode) with
                {
                    Code = EndpointRpcErrorCode.RuntimeUnavailable,
                    RequiresRestart = true
                },
            _ when requiresProcessRestart =>
                Error(
                    EndpointRpcErrorCode.RuntimeUnavailable,
                    EndpointFailureKind.Generic,
                    "The backend runtime requires process replacement.",
                    requiresRestart: true),
            InvalidInputException invalidInputException =>
                Error(
                    EndpointRpcErrorCode.ValidationFailed,
                    MapInvalidInputFailure(invalidInputException, context.OperationId),
                    "The endpoint input is invalid."),
            ArgumentException =>
                Error(
                    EndpointRpcErrorCode.ValidationFailed,
                    EndpointFailureKind.InvalidInput,
                    "The endpoint input is invalid."),
            SyncRouteDisabledException =>
                Error(
                    EndpointRpcErrorCode.OperationRejected,
                    EndpointFailureKind.Generic,
                    "The endpoint operation was rejected."),
            UserNotFoundException or UsernameChangedDuringLoginException
                when context.OperationId == EndpointOperationId.Login =>
                Error(
                    EndpointRpcErrorCode.AuthenticationFailed,
                    EndpointFailureKind.InvalidCredentials,
                    "Authentication failed."),
            UnauthorizedAccessException when context.OperationId == EndpointOperationId.Login =>
                Error(
                    EndpointRpcErrorCode.AuthenticationFailed,
                    EndpointFailureKind.InvalidCredentials,
                    "Authentication failed."),
            InvalidTokenException =>
                Error(
                    EndpointRpcErrorCode.AuthorizationFailed,
                    EndpointFailureKind.InvalidSession,
                    "The endpoint authorization is invalid."),
            UnauthorizedAccessException =>
                Error(
                    EndpointRpcErrorCode.AuthorizationFailed,
                    EndpointFailureKind.InvalidSession,
                    "The endpoint operation is not authorized."),
            UserNotFoundException or PasswordNotFoundException or PasswordTagNotFoundException or
                CustomUserColorNotFoundException =>
                Error(
                    EndpointRpcErrorCode.NotFound,
                    EndpointFailureKind.NotFound,
                    "The requested endpoint resource was not found."),
            DuplicatePasswordNameException =>
                Error(
                    EndpointRpcErrorCode.Conflict,
                    EndpointFailureKind.DuplicatePasswordName,
                    "The endpoint operation conflicts with current state."),
            DuplicatePasswordTagNameException =>
                Error(
                    EndpointRpcErrorCode.Conflict,
                    EndpointFailureKind.DuplicatePasswordTagName,
                    "The endpoint operation conflicts with current state."),
            DuplicateCustomUserColorNameException =>
                Error(
                    EndpointRpcErrorCode.Conflict,
                    EndpointFailureKind.DuplicateCustomColorName,
                    "The endpoint operation conflicts with current state."),
            DuplicateCustomUserColorCodeException =>
                Error(
                    EndpointRpcErrorCode.Conflict,
                    EndpointFailureKind.DuplicateCustomColorCode,
                    "The endpoint operation conflicts with current state."),
            DeterministicSyncConflictException or RecoveryEvidenceConflictException =>
                Error(
                    EndpointRpcErrorCode.Conflict,
                    EndpointFailureKind.Generic,
                    "The endpoint operation conflicts with current state."),
            LimitReachedException =>
                Error(
                    EndpointRpcErrorCode.OperationRejected,
                    EndpointFailureKind.LimitReached,
                    "The endpoint operation was rejected."),
            DeviceEnrollmentException enrollmentException =>
                MapDeviceEnrollmentError(enrollmentException.ErrorCode),
            DeviceIdentityNotInitilaizedException =>
                Error(
                    EndpointRpcErrorCode.RuntimeUnavailable,
                    EndpointFailureKind.DeviceIdentityUnavailable,
                    "The backend runtime is temporarily unavailable."),
            KeyProtectorUnavailableException =>
                Error(
                    EndpointRpcErrorCode.RuntimeUnavailable,
                    EndpointFailureKind.Generic,
                    "The backend runtime is temporarily unavailable."),
            InvalidDataIntegrityException =>
                Error(
                    EndpointRpcErrorCode.BackendFailure,
                    EndpointFailureKind.DataIntegrity,
                    "The backend could not safely complete the operation."),
            CryptographicException =>
                Error(
                    EndpointRpcErrorCode.BackendFailure,
                    EndpointFailureKind.Generic,
                    "The backend could not safely complete the operation."),
            InvalidOperationException =>
                Error(
                    EndpointRpcErrorCode.InteractiveSessionUnavailable,
                    EndpointFailureKind.Generic,
                    "The interactive backend session is unavailable."),
            _ => Error(
                EndpointRpcErrorCode.BackendFailure,
                EndpointFailureKind.Generic,
                "The endpoint operation failed.")
        };

        return CreateError(metadata, context.OperationId);
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
            EndpointFailureKind.Generic,
            "The endpoint operation may have executed, but its outcome could not be confirmed.",
            requiresProcessRestart,
            EndpointMutationOutcome.OutcomeUnknown);
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

        var failureKind = EndpointFailureKind.Generic;
        DeviceEnrollmentErrorCode? enrollmentErrorCode = null;
        var safeMessage = "The endpoint operation committed authoritative state, but required follow-up work did not complete.";
        if (context.OperationId == EndpointOperationId.AddDeviceByCode)
        {
            if (exception is not DeviceEnrollmentPartiallyCommittedException enrollmentException)
                return CreateOutcomeUnknown(context, requiresProcessRestart);

            failureKind = EndpointFailureKind.DeviceEnrollment;
            enrollmentErrorCode = enrollmentException.ErrorCode;
            safeMessage = "The device addition was committed, but enrollment recovery is required.";
        }
        else if (exception is DeviceEnrollmentPartiallyCommittedException)
        {
            return CreateOutcomeUnknown(context, requiresProcessRestart);
        }

        return new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            failureKind,
            safeMessage,
            requiresProcessRestart,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            enrollmentErrorCode);
    }

    private static EndpointRpcError CreateError(
        ErrorMetadata metadata,
        EndpointOperationId operationId) =>
        new(
            metadata.Code,
            metadata.FailureKind,
            metadata.Message,
            metadata.RequiresRestart,
            GetConclusiveMutationOutcome(operationId),
            metadata.EnrollmentErrorCode);

    private static EndpointMutationOutcome GetConclusiveMutationOutcome(EndpointOperationId operationId) =>
        EndpointOperationManifest.Get(operationId).MutatesState
            ? EndpointMutationOutcome.NotCommitted
            : EndpointMutationOutcome.NotApplicable;

    private static EndpointFailureKind MapInvalidInputFailure(
        InvalidInputException exception,
        EndpointOperationId operationId)
    {
        if (operationId != EndpointOperationId.Register)
            return EndpointFailureKind.InvalidInput;

        if (exception.Errors.Contains(
            RegistrationValidationErrors.UsernameUnavailable,
            StringComparer.Ordinal))
        {
            return EndpointFailureKind.RegistrationUsernameUnavailable;
        }

        if (exception.Errors.Contains(
            RegistrationValidationErrors.UsernameAvailabilityIndeterminate,
            StringComparer.Ordinal))
        {
            return EndpointFailureKind.RegistrationUsernameAvailabilityIndeterminate;
        }

        return EndpointFailureKind.InvalidInput;
    }

    private static ErrorMetadata MapDeviceEnrollmentError(DeviceEnrollmentErrorCode errorCode)
    {
        var code = errorCode switch
        {
            DeviceEnrollmentErrorCode.Unknown => EndpointRpcErrorCode.BackendFailure,
            DeviceEnrollmentErrorCode.NewDeviceNotFound => EndpointRpcErrorCode.NotFound,
            DeviceEnrollmentErrorCode.DeviceIdentityConflict => EndpointRpcErrorCode.Conflict,
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed or
                DeviceEnrollmentErrorCode.LocalNetworkUnavailable or
                DeviceEnrollmentErrorCode.LocalEnrollmentListenerUnavailable =>
                    EndpointRpcErrorCode.RuntimeUnavailable,
            _ => EndpointRpcErrorCode.OperationRejected
        };

        return Error(
            code,
            EndpointFailureKind.DeviceEnrollment,
            "The device enrollment operation failed.",
            enrollmentErrorCode: errorCode);
    }

    private static ErrorMetadata Error(
        EndpointRpcErrorCode code,
        EndpointFailureKind failureKind,
        string message,
        bool requiresRestart = false,
        DeviceEnrollmentErrorCode? enrollmentErrorCode = null) =>
        new(code, failureKind, message, requiresRestart, enrollmentErrorCode);

    private sealed record ErrorMetadata(
        EndpointRpcErrorCode Code,
        EndpointFailureKind FailureKind,
        string Message,
        bool RequiresRestart,
        DeviceEnrollmentErrorCode? EnrollmentErrorCode);
}
