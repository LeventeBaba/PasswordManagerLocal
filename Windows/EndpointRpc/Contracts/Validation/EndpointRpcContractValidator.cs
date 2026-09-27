using PasswordManagerLocal.Common.Contracts.Devices;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Validation;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;

namespace PasswordManagerLocal.Windows.EndpointRpc.Validation;

public sealed class EndpointRpcContractValidator
{
    private static readonly IReadOnlySet<Type> RequestTypes = new HashSet<Type>
    {
        typeof(RegisterEndpointRequest),
        typeof(LoginEndpointRequest),
        typeof(RenewAuthSessionEndpointRequest),
        typeof(LogoutEndpointRequest),
        typeof(GetAuthSessionStatusEndpointRequest),
        typeof(ChangeMasterPasswordEndpointRequest),
        typeof(GetUserProfileInfoEndpointRequest),
        typeof(DeleteUserAccountEndpointRequest),
        typeof(ChangeUsernameEndpointRequest),
        typeof(UpdateUserProfileInfoEndpointRequest),
        typeof(GetLocalDeviceInfoEndpointRequest),
        typeof(GetLocalUserSyncOnEndpointRequest),
        typeof(SetLocalUserSyncOnEndpointRequest),
        typeof(SetLocalDeviceNameEndpointRequest),
        typeof(GetUserDevicesEndpointRequest),
        typeof(SetUserDeviceNameEndpointRequest),
        typeof(SetUserDeviceSyncOnEndpointRequest),
        typeof(UnblockUserDeviceEndpointRequest),
        typeof(DisconnectUserDeviceEndpointRequest),
        typeof(StartDeviceEnrollmentEndpointRequest),
        typeof(GetDeviceEnrollmentStatusEndpointRequest),
        typeof(CancelDeviceEnrollmentEndpointRequest),
        typeof(AddDeviceByCodeEndpointRequest),
        typeof(RestoreRememberedSessionsEndpointRequest),
        typeof(InitializeRememberMeSessionEndpointRequest),
        typeof(SetRememberMeEndpointRequest),
        typeof(GetSavedPasswordsEndpointRequest),
        typeof(AddNewPasswordEndpointRequest),
        typeof(RemovePasswordsEndpointRequest),
        typeof(GetUnsecurePasswordEndpointRequest),
        typeof(UpdatePasswordEndpointRequest),
        typeof(ExportPasswordsToUserEndpointRequest),
        typeof(AddCustomUserColorsEndpointRequest),
        typeof(DeleteCustomUserColorsEndpointRequest),
        typeof(ExportCustomUserColorsToUserEndpointRequest),
        typeof(UpdateCustomUserColorEndpointRequest),
        typeof(AddPasswordTagEndpointRequest),
        typeof(DeletePasswordTagEndpointRequest),
        typeof(ExportPasswordTagsToUserEndpointRequest),
        typeof(UpdatePasswordTagEndpointRequest),
    };

    private static readonly IReadOnlySet<Type> ResponseTypes = new HashSet<Type>
    {
        typeof(RegisterEndpointResponse),
        typeof(LoginEndpointResponse),
        typeof(RenewAuthSessionEndpointResponse),
        typeof(LogoutEndpointResponse),
        typeof(GetAuthSessionStatusEndpointResponse),
        typeof(ChangeMasterPasswordEndpointResponse),
        typeof(GetUserProfileInfoEndpointResponse),
        typeof(DeleteUserAccountEndpointResponse),
        typeof(ChangeUsernameEndpointResponse),
        typeof(UpdateUserProfileInfoEndpointResponse),
        typeof(GetLocalDeviceInfoEndpointResponse),
        typeof(GetLocalUserSyncOnEndpointResponse),
        typeof(SetLocalUserSyncOnEndpointResponse),
        typeof(SetLocalDeviceNameEndpointResponse),
        typeof(GetUserDevicesEndpointResponse),
        typeof(SetUserDeviceNameEndpointResponse),
        typeof(SetUserDeviceSyncOnEndpointResponse),
        typeof(UnblockUserDeviceEndpointResponse),
        typeof(DisconnectUserDeviceEndpointResponse),
        typeof(StartDeviceEnrollmentEndpointResponse),
        typeof(GetDeviceEnrollmentStatusEndpointResponse),
        typeof(CancelDeviceEnrollmentEndpointResponse),
        typeof(AddDeviceByCodeEndpointResponse),
        typeof(RestoreRememberedSessionsEndpointResponse),
        typeof(InitializeRememberMeSessionEndpointResponse),
        typeof(SetRememberMeEndpointResponse),
        typeof(GetSavedPasswordsEndpointResponse),
        typeof(AddNewPasswordEndpointResponse),
        typeof(RemovePasswordsEndpointResponse),
        typeof(GetUnsecurePasswordEndpointResponse),
        typeof(UpdatePasswordEndpointResponse),
        typeof(ExportPasswordsToUserEndpointResponse),
        typeof(AddCustomUserColorsEndpointResponse),
        typeof(DeleteCustomUserColorsEndpointResponse),
        typeof(ExportCustomUserColorsToUserEndpointResponse),
        typeof(UpdateCustomUserColorEndpointResponse),
        typeof(AddPasswordTagEndpointResponse),
        typeof(DeletePasswordTagEndpointResponse),
        typeof(ExportPasswordTagsToUserEndpointResponse),
        typeof(UpdatePasswordTagEndpointResponse),
    };

    public bool CanValidateRequest(Type type) => RequestTypes.Contains(type);
    public bool CanValidateResponse(Type type) => ResponseTypes.Contains(type);

    public void ValidateRequest(EndpointOperationId operationId, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool valid;
        try
        {
            valid = operationId switch
            {
            EndpointOperationId.Register when value is RegisterEndpointRequest request => Validate(request),
            EndpointOperationId.Login when value is LoginEndpointRequest request => Validate(request),
            EndpointOperationId.RenewAuthSession when value is RenewAuthSessionEndpointRequest request => Validate(request),
            EndpointOperationId.Logout when value is LogoutEndpointRequest request => Validate(request),
            EndpointOperationId.GetAuthSessionStatus when value is GetAuthSessionStatusEndpointRequest request => Validate(request),
            EndpointOperationId.ChangeMasterPassword when value is ChangeMasterPasswordEndpointRequest request => Validate(request),
            EndpointOperationId.GetUserProfileInfo when value is GetUserProfileInfoEndpointRequest request => Validate(request),
            EndpointOperationId.DeleteUserAccount when value is DeleteUserAccountEndpointRequest request => Validate(request),
            EndpointOperationId.ChangeUsername when value is ChangeUsernameEndpointRequest request => Validate(request),
            EndpointOperationId.UpdateUserProfileInfo when value is UpdateUserProfileInfoEndpointRequest request => Validate(request),
            EndpointOperationId.GetLocalDeviceInfo when value is GetLocalDeviceInfoEndpointRequest request => Validate(request),
            EndpointOperationId.GetLocalUserSyncOn when value is GetLocalUserSyncOnEndpointRequest request => Validate(request),
            EndpointOperationId.SetLocalUserSyncOn when value is SetLocalUserSyncOnEndpointRequest request => Validate(request),
            EndpointOperationId.SetLocalDeviceName when value is SetLocalDeviceNameEndpointRequest request => Validate(request),
            EndpointOperationId.GetUserDevices when value is GetUserDevicesEndpointRequest request => Validate(request),
            EndpointOperationId.SetUserDeviceName when value is SetUserDeviceNameEndpointRequest request => Validate(request),
            EndpointOperationId.SetUserDeviceSyncOn when value is SetUserDeviceSyncOnEndpointRequest request => Validate(request),
            EndpointOperationId.UnblockUserDevice when value is UnblockUserDeviceEndpointRequest request => Validate(request),
            EndpointOperationId.DisconnectUserDevice when value is DisconnectUserDeviceEndpointRequest request => Validate(request),
            EndpointOperationId.StartDeviceEnrollment when value is StartDeviceEnrollmentEndpointRequest request => Validate(request),
            EndpointOperationId.GetDeviceEnrollmentStatus when value is GetDeviceEnrollmentStatusEndpointRequest request => Validate(request),
            EndpointOperationId.CancelDeviceEnrollment when value is CancelDeviceEnrollmentEndpointRequest request => Validate(request),
            EndpointOperationId.AddDeviceByCode when value is AddDeviceByCodeEndpointRequest request => Validate(request),
            EndpointOperationId.RestoreRememberedSessions when value is RestoreRememberedSessionsEndpointRequest request => Validate(request),
            EndpointOperationId.InitializeRememberMeSession when value is InitializeRememberMeSessionEndpointRequest request => Validate(request),
            EndpointOperationId.SetRememberMe when value is SetRememberMeEndpointRequest request => Validate(request),
            EndpointOperationId.GetSavedPasswords when value is GetSavedPasswordsEndpointRequest request => Validate(request),
            EndpointOperationId.AddNewPassword when value is AddNewPasswordEndpointRequest request => Validate(request),
            EndpointOperationId.RemovePasswords when value is RemovePasswordsEndpointRequest request => Validate(request),
            EndpointOperationId.GetUnsecurePassword when value is GetUnsecurePasswordEndpointRequest request => Validate(request),
            EndpointOperationId.UpdatePassword when value is UpdatePasswordEndpointRequest request => Validate(request),
            EndpointOperationId.ExportPasswordsToUser when value is ExportPasswordsToUserEndpointRequest request => Validate(request),
            EndpointOperationId.AddCustomUserColors when value is AddCustomUserColorsEndpointRequest request => Validate(request),
            EndpointOperationId.DeleteCustomUserColors when value is DeleteCustomUserColorsEndpointRequest request => Validate(request),
            EndpointOperationId.ExportCustomUserColorsToUser when value is ExportCustomUserColorsToUserEndpointRequest request => Validate(request),
            EndpointOperationId.UpdateCustomUserColor when value is UpdateCustomUserColorEndpointRequest request => Validate(request),
            EndpointOperationId.AddPasswordTag when value is AddPasswordTagEndpointRequest request => Validate(request),
            EndpointOperationId.DeletePasswordTag when value is DeletePasswordTagEndpointRequest request => Validate(request),
            EndpointOperationId.ExportPasswordTagsToUser when value is ExportPasswordTagsToUserEndpointRequest request => Validate(request),
            EndpointOperationId.UpdatePasswordTag when value is UpdatePasswordTagEndpointRequest request => Validate(request),
                _ => false
            };
        }
        catch (Exception exception) when (exception is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            valid = false;
        }

        if (!valid)
            throw new EndpointRpcPayloadException("The endpoint RPC request is semantically invalid.");
    }

    public void ValidateResponse(EndpointOperationId operationId, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool valid;
        try
        {
            valid = operationId switch
            {
            EndpointOperationId.Register when value is RegisterEndpointResponse response => Validate(response),
            EndpointOperationId.Login when value is LoginEndpointResponse response => Validate(response),
            EndpointOperationId.RenewAuthSession when value is RenewAuthSessionEndpointResponse response => Validate(response),
            EndpointOperationId.Logout when value is LogoutEndpointResponse response => Validate(response),
            EndpointOperationId.GetAuthSessionStatus when value is GetAuthSessionStatusEndpointResponse response => Validate(response),
            EndpointOperationId.ChangeMasterPassword when value is ChangeMasterPasswordEndpointResponse response => Validate(response),
            EndpointOperationId.GetUserProfileInfo when value is GetUserProfileInfoEndpointResponse response => Validate(response),
            EndpointOperationId.DeleteUserAccount when value is DeleteUserAccountEndpointResponse response => Validate(response),
            EndpointOperationId.ChangeUsername when value is ChangeUsernameEndpointResponse response => Validate(response),
            EndpointOperationId.UpdateUserProfileInfo when value is UpdateUserProfileInfoEndpointResponse response => Validate(response),
            EndpointOperationId.GetLocalDeviceInfo when value is GetLocalDeviceInfoEndpointResponse response => Validate(response),
            EndpointOperationId.GetLocalUserSyncOn when value is GetLocalUserSyncOnEndpointResponse response => Validate(response),
            EndpointOperationId.SetLocalUserSyncOn when value is SetLocalUserSyncOnEndpointResponse response => Validate(response),
            EndpointOperationId.SetLocalDeviceName when value is SetLocalDeviceNameEndpointResponse response => Validate(response),
            EndpointOperationId.GetUserDevices when value is GetUserDevicesEndpointResponse response => Validate(response),
            EndpointOperationId.SetUserDeviceName when value is SetUserDeviceNameEndpointResponse response => Validate(response),
            EndpointOperationId.SetUserDeviceSyncOn when value is SetUserDeviceSyncOnEndpointResponse response => Validate(response),
            EndpointOperationId.UnblockUserDevice when value is UnblockUserDeviceEndpointResponse response => Validate(response),
            EndpointOperationId.DisconnectUserDevice when value is DisconnectUserDeviceEndpointResponse response => Validate(response),
            EndpointOperationId.StartDeviceEnrollment when value is StartDeviceEnrollmentEndpointResponse response => Validate(response),
            EndpointOperationId.GetDeviceEnrollmentStatus when value is GetDeviceEnrollmentStatusEndpointResponse response => Validate(response),
            EndpointOperationId.CancelDeviceEnrollment when value is CancelDeviceEnrollmentEndpointResponse response => Validate(response),
            EndpointOperationId.AddDeviceByCode when value is AddDeviceByCodeEndpointResponse response => Validate(response),
            EndpointOperationId.RestoreRememberedSessions when value is RestoreRememberedSessionsEndpointResponse response => Validate(response),
            EndpointOperationId.InitializeRememberMeSession when value is InitializeRememberMeSessionEndpointResponse response => Validate(response),
            EndpointOperationId.SetRememberMe when value is SetRememberMeEndpointResponse response => Validate(response),
            EndpointOperationId.GetSavedPasswords when value is GetSavedPasswordsEndpointResponse response => Validate(response),
            EndpointOperationId.AddNewPassword when value is AddNewPasswordEndpointResponse response => Validate(response),
            EndpointOperationId.RemovePasswords when value is RemovePasswordsEndpointResponse response => Validate(response),
            EndpointOperationId.GetUnsecurePassword when value is GetUnsecurePasswordEndpointResponse response => Validate(response),
            EndpointOperationId.UpdatePassword when value is UpdatePasswordEndpointResponse response => Validate(response),
            EndpointOperationId.ExportPasswordsToUser when value is ExportPasswordsToUserEndpointResponse response => Validate(response),
            EndpointOperationId.AddCustomUserColors when value is AddCustomUserColorsEndpointResponse response => Validate(response),
            EndpointOperationId.DeleteCustomUserColors when value is DeleteCustomUserColorsEndpointResponse response => Validate(response),
            EndpointOperationId.ExportCustomUserColorsToUser when value is ExportCustomUserColorsToUserEndpointResponse response => Validate(response),
            EndpointOperationId.UpdateCustomUserColor when value is UpdateCustomUserColorEndpointResponse response => Validate(response),
            EndpointOperationId.AddPasswordTag when value is AddPasswordTagEndpointResponse response => Validate(response),
            EndpointOperationId.DeletePasswordTag when value is DeletePasswordTagEndpointResponse response => Validate(response),
            EndpointOperationId.ExportPasswordTagsToUser when value is ExportPasswordTagsToUserEndpointResponse response => Validate(response),
            EndpointOperationId.UpdatePasswordTag when value is UpdatePasswordTagEndpointResponse response => Validate(response),
                _ => false
            };
        }
        catch (Exception exception) when (exception is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            valid = false;
        }

        if (!valid)
            throw new EndpointRpcPayloadException("The endpoint RPC response is semantically invalid.");
    }

    public void Validate(EndpointRpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Enum.IsDefined(error.ErrorCode) ||
            !Enum.IsDefined(error.ErrorCategory) ||
            !Enum.IsDefined(error.MutationOutcome) ||
            error.CorrelationId <= 0 ||
            string.IsNullOrWhiteSpace(error.SafeMessage) ||
            error.SafeMessage.Length > EndpointRpcLimits.MaximumSafeErrorMessageLength ||
            error.OccurredAtUtc.Offset != TimeSpan.Zero ||
            !HasExpectedErrorCategory(error.ErrorCode, error.ErrorCategory) ||
            !HasValidMutationOutcome(error) ||
            (error.RequiresProcessRestart &&
                error.ErrorCode is not EndpointRpcErrorCode.RuntimeUnavailable and
                    not EndpointRpcErrorCode.OperationOutcomeUnknown and
                    not EndpointRpcErrorCode.OperationPartiallyCommitted))
        {
            throw new EndpointRpcPayloadException("The endpoint RPC error is semantically invalid.");
        }
    }

    public void Validate(EndpointOperationId operationId, EndpointRpcError error)
    {
        Validate(error);
        var descriptor = EndpointOperationManifest.Get(operationId);
        var valid = descriptor.MutatesState
            ? HasValidMutationOutcomeForMutation(operationId, descriptor, error)
            : error.MutationOutcome == EndpointMutationOutcome.NotApplicable && error.Recovery is null;

        if (!valid)
            throw new EndpointRpcPayloadException("The endpoint RPC error does not match the operation mutation policy.");
    }

    private static bool HasValidMutationOutcome(EndpointRpcError error) => error.ErrorCode switch
    {
        EndpointRpcErrorCode.OperationPartiallyCommitted =>
            error.MutationOutcome == EndpointMutationOutcome.PartiallyCommittedRecoveryRequired &&
            !error.IsRetryable &&
            (error.Recovery is null || ValidRecovery(error.Recovery)),
        EndpointRpcErrorCode.OperationOutcomeUnknown =>
            error.MutationOutcome == EndpointMutationOutcome.OutcomeUnknown &&
            !error.IsRetryable &&
            error.Recovery is null,
        _ =>
            error.MutationOutcome is EndpointMutationOutcome.NotApplicable or EndpointMutationOutcome.NotCommitted &&
            error.Recovery is null
    };

    private static bool HasValidMutationOutcomeForMutation(
        EndpointOperationId operationId,
        EndpointOperationDescriptor descriptor,
        EndpointRpcError error)
    {
        if (error.ErrorCode == EndpointRpcErrorCode.OperationPartiallyCommitted)
        {
            if (!descriptor.CanPartiallyCommit)
                return false;

            return operationId == EndpointOperationId.AddDeviceByCode
                ? ValidRecovery(error.Recovery) && error.Recovery!.RecoveryKind == EndpointRecoveryKind.DeviceEnrollment
                : error.Recovery is null;
        }

        if (error.ErrorCode == EndpointRpcErrorCode.OperationOutcomeUnknown)
            return error.MutationOutcome == EndpointMutationOutcome.OutcomeUnknown;

        return error.MutationOutcome == EndpointMutationOutcome.NotCommitted && error.Recovery is null;
    }

    private static bool ValidRecovery(EndpointRecoveryMetadata? recovery) =>
        recovery is not null &&
        recovery.RecoveryKind == EndpointRecoveryKind.DeviceEnrollment &&
        recovery.TargetDeviceId != Guid.Empty &&
        recovery.TargetOriginInstanceId != Guid.Empty &&
        recovery.EnrollmentCommitId != Guid.Empty &&
        recovery.RecoveryAvailable &&
        recovery.RequiresSignedRemovalToUndo;


    private static bool HasExpectedErrorCategory(
        EndpointRpcErrorCode errorCode,
        EndpointRpcErrorCategory errorCategory) => errorCode switch
    {
        EndpointRpcErrorCode.AuthenticationFailed => errorCategory == EndpointRpcErrorCategory.Authentication,
        EndpointRpcErrorCode.AuthorizationFailed => errorCategory == EndpointRpcErrorCategory.Authorization,
        EndpointRpcErrorCode.ValidationFailed or EndpointRpcErrorCode.OperationRejected or
            EndpointRpcErrorCode.RequestPayloadTooLarge or EndpointRpcErrorCode.ResponsePayloadTooLarge or
            EndpointRpcErrorCode.UnknownOperation => errorCategory == EndpointRpcErrorCategory.Validation,
        EndpointRpcErrorCode.NotFound => errorCategory == EndpointRpcErrorCategory.NotFound,
        EndpointRpcErrorCode.Conflict => errorCategory == EndpointRpcErrorCategory.Conflict,
        EndpointRpcErrorCode.InteractiveSessionUnavailable or EndpointRpcErrorCode.RuntimeUnavailable or
            EndpointRpcErrorCode.Disconnected => errorCategory == EndpointRpcErrorCategory.Availability,
        EndpointRpcErrorCode.OperationCancelled => errorCategory == EndpointRpcErrorCategory.Cancellation,
        EndpointRpcErrorCode.OperationPartiallyCommitted => errorCategory == EndpointRpcErrorCategory.Recovery,
        EndpointRpcErrorCode.OperationOutcomeUnknown or
            EndpointRpcErrorCode.BackendFailure or
            EndpointRpcErrorCode.EndpointCorrelationMismatch =>
                errorCategory == EndpointRpcErrorCategory.Internal,
        _ => false
    };

    private static bool Validate(RegisterEndpointRequest value) =>
        value.Request is not null && ValidSensitive(value.Request.Password) && value.Request.Validate(out _);
    private static bool Validate(LoginEndpointRequest value) =>
        value.Request is not null && ValidSensitive(value.Request.Password) && value.Request.Validate();
    private static bool Validate(RenewAuthSessionEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(LogoutEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(GetAuthSessionStatusEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(ChangeMasterPasswordEndpointRequest value) =>
        value.Request is not null && ValidGuid(value.Request.Token) &&
        ValidSensitive(value.Request.Password) && ValidSensitive(value.Request.NewPassword) &&
        value.Request.Validate(out _);
    private static bool Validate(GetUserProfileInfoEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(DeleteUserAccountEndpointRequest value) => ValidGuid(value.Token) && ValidSensitive(value.Password);
    private static bool Validate(ChangeUsernameEndpointRequest value) => ValidGuid(value.Token) && DataValidation.IsValidUsername(value.NewUsername);
    private static bool Validate(UpdateUserProfileInfoEndpointRequest value) => value.Request is not null && ValidGuid(value.Request.Token) && value.Request.Validate(out _);
    private static bool Validate(GetLocalDeviceInfoEndpointRequest value) => true;
    private static bool Validate(GetLocalUserSyncOnEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(SetLocalUserSyncOnEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(SetLocalDeviceNameEndpointRequest value) => ValidGuid(value.Token) && DataValidation.IsValidUserDeviceName(value.Name);
    private static bool Validate(GetUserDevicesEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(SetUserDeviceNameEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.DeviceId) && DataValidation.IsValidUserDeviceName(value.Name);
    private static bool Validate(SetUserDeviceSyncOnEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.DeviceId);
    private static bool Validate(UnblockUserDeviceEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.DeviceId);
    private static bool Validate(DisconnectUserDeviceEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.DeviceId) && ValidSensitive(value.MasterPassword);
    private static bool Validate(StartDeviceEnrollmentEndpointRequest value) => true;
    private static bool Validate(GetDeviceEnrollmentStatusEndpointRequest value) => true;
    private static bool Validate(CancelDeviceEnrollmentEndpointRequest value) => true;
    private static bool Validate(AddDeviceByCodeEndpointRequest value) => ValidGuid(value.Token) && ValidString(value.Code, 1, EndpointRpcLimits.MaximumEnrollmentCodeLength);
    private static bool Validate(RestoreRememberedSessionsEndpointRequest value) => true;
    private static bool Validate(InitializeRememberMeSessionEndpointRequest value) => ValidGuid(value.UserId);
    private static bool Validate(SetRememberMeEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(GetSavedPasswordsEndpointRequest value) => ValidGuid(value.Token);
    private static bool Validate(AddNewPasswordEndpointRequest value) =>
        ValidGuid(value.Token) && value.Request is not null && ValidSensitive(value.Request.Password) &&
        ValidIds(value.Request.TagIds, requireNonEmpty: false) && value.Request.Validate(out _);
    private static bool Validate(RemovePasswordsEndpointRequest value) => ValidGuid(value.Token) && ValidIds(value.PasswordIds, requireNonEmpty: true);
    private static bool Validate(GetUnsecurePasswordEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.PasswordId);
    private static bool Validate(UpdatePasswordEndpointRequest value) =>
        ValidGuid(value.Token) && value.Request is not null && value.Request.Id != Guid.Empty &&
        (value.Request.Password is null || ValidSensitive(value.Request.Password)) &&
        (value.Request.TagIds is null || ValidIds(value.Request.TagIds, requireNonEmpty: false)) &&
        value.Request.Validate(out _);
    private static bool Validate(ExportPasswordsToUserEndpointRequest value) => ValidGuid(value.SourceToken) && value.Request is not null && value.Request.Validate(out _);
    private static bool Validate(AddCustomUserColorsEndpointRequest value) => ValidGuid(value.Token) && ValidColorRequests(value.Requests);
    private static bool Validate(DeleteCustomUserColorsEndpointRequest value) => ValidGuid(value.Token) && ValidIds(value.CustomUserColorIds, requireNonEmpty: true);
    private static bool Validate(ExportCustomUserColorsToUserEndpointRequest value) => ValidGuid(value.SourceToken) && value.Request is not null && value.Request.Validate(out _);
    private static bool Validate(UpdateCustomUserColorEndpointRequest value) => ValidGuid(value.Token) && value.Request is not null && value.Request.Validate(out _);
    private static bool Validate(AddPasswordTagEndpointRequest value) => ValidGuid(value.Token) && value.Request is not null && value.Request.Validate(out _);
    private static bool Validate(DeletePasswordTagEndpointRequest value) => ValidGuid(value.Token) && ValidGuid(value.PasswordTagId);
    private static bool Validate(ExportPasswordTagsToUserEndpointRequest value) => ValidGuid(value.SourceToken) && value.Request is not null && value.Request.Validate(out _);
    private static bool Validate(UpdatePasswordTagEndpointRequest value) => ValidGuid(value.Token) && value.Request is not null && value.Request.Validate(out _);

    private static bool Validate(RegisterEndpointResponse value) => ValidGuid(value.Token);
    private static bool Validate(LoginEndpointResponse value) => ValidGuid(value.Token);
    private static bool Validate(RenewAuthSessionEndpointResponse value) => ValidGuid(value.RenewedToken);
    private static bool Validate(LogoutEndpointResponse value) => true;
    private static bool Validate(GetAuthSessionStatusEndpointResponse value) => ValidAuthSessionStatus(value.Status);
    private static bool Validate(ChangeMasterPasswordEndpointResponse value) => true;
    private static bool Validate(GetUserProfileInfoEndpointResponse value) => ValidProfile(value.Profile);
    private static bool Validate(DeleteUserAccountEndpointResponse value) => true;
    private static bool Validate(ChangeUsernameEndpointResponse value) => true;
    private static bool Validate(UpdateUserProfileInfoEndpointResponse value) => true;
    private static bool Validate(GetLocalDeviceInfoEndpointResponse value) => ValidLocalDevice(value.Device);
    private static bool Validate(GetLocalUserSyncOnEndpointResponse value) => true;
    private static bool Validate(SetLocalUserSyncOnEndpointResponse value) => true;
    private static bool Validate(SetLocalDeviceNameEndpointResponse value) => true;
    private static bool Validate(GetUserDevicesEndpointResponse value) =>
        value.Devices is not null && value.Devices.Count <= EndpointRpcLimits.MaximumCollectionItems &&
        value.Devices.All(ValidUserDevice) &&
        value.Devices.Select(device => device.DeviceId).Distinct().Count() == value.Devices.Count;
    private static bool Validate(SetUserDeviceNameEndpointResponse value) => true;
    private static bool Validate(SetUserDeviceSyncOnEndpointResponse value) => true;
    private static bool Validate(UnblockUserDeviceEndpointResponse value) => true;
    private static bool Validate(DisconnectUserDeviceEndpointResponse value) => ValidRemovalResult(value.Result);
    private static bool Validate(StartDeviceEnrollmentEndpointResponse value) => ValidEnrollmentCode(value.Enrollment);
    private static bool Validate(GetDeviceEnrollmentStatusEndpointResponse value) => ValidEnrollmentStatus(value.Status);
    private static bool Validate(CancelDeviceEnrollmentEndpointResponse value) => true;
    private static bool Validate(AddDeviceByCodeEndpointResponse value) => true;
    private static bool Validate(RestoreRememberedSessionsEndpointResponse value) => ValidIds(value.Tokens, requireNonEmpty: false);
    private static bool Validate(InitializeRememberMeSessionEndpointResponse value) => ValidGuid(value.Token);
    private static bool Validate(SetRememberMeEndpointResponse value) => true;
    private static bool Validate(GetSavedPasswordsEndpointResponse value) => ValidSavedPasswords(value.Passwords);
    private static bool Validate(AddNewPasswordEndpointResponse value) => true;
    private static bool Validate(RemovePasswordsEndpointResponse value) => true;
    private static bool Validate(GetUnsecurePasswordEndpointResponse value) => ValidSensitive(value.Password);
    private static bool Validate(UpdatePasswordEndpointResponse value) => true;
    private static bool Validate(ExportPasswordsToUserEndpointResponse value) => true;
    private static bool Validate(AddCustomUserColorsEndpointResponse value) => true;
    private static bool Validate(DeleteCustomUserColorsEndpointResponse value) => true;
    private static bool Validate(ExportCustomUserColorsToUserEndpointResponse value) => true;
    private static bool Validate(UpdateCustomUserColorEndpointResponse value) => true;
    private static bool Validate(AddPasswordTagEndpointResponse value) => true;
    private static bool Validate(DeletePasswordTagEndpointResponse value) => true;
    private static bool Validate(ExportPasswordTagsToUserEndpointResponse value) => true;
    private static bool Validate(UpdatePasswordTagEndpointResponse value) => true;

    private static bool ValidGuid(Guid value) => value != Guid.Empty;

    private static bool ValidSensitive(byte[]? value) =>
        value is not null && value.Length >= 1 && value.Length <= EndpointRpcLimits.MaximumSensitiveBinaryFieldSize;

    private static bool ValidString(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum;

    private static bool ValidIds(IReadOnlyList<Guid>? values, bool requireNonEmpty)
    {
        if (values is null || values.Count > EndpointRpcLimits.MaximumCollectionItems ||
            (requireNonEmpty && values.Count == 0) || values.Any(id => id == Guid.Empty))
        {
            return false;
        }
        return values.Distinct().Count() == values.Count;
    }

    private static bool ValidColorRequests(IReadOnlyList<NewCustomUserColorRequest>? values) =>
        values is not null && values.Count > 0 && values.Count <= EndpointRpcLimits.MaximumCollectionItems &&
        values.All(value => value is not null && value.Validate(out _));

    private static bool ValidAuthSessionStatus(AuthSessionStatusResponse? value) =>
        value is not null && Enum.IsDefined(value.InvalidationReason) &&
        (!value.ExpiresAtUtc.HasValue || value.ExpiresAtUtc.Value.Offset == TimeSpan.Zero);

    private static bool ValidProfile(UserProfileInfoResponse? value) =>
        value is not null && ValidGuid(value.UId) &&
        DataValidation.IsValidUsername(value.Username) &&
        DataValidation.IsValidFirstName(value.FirstName) &&
        DataValidation.IsValidLastName(value.LastName) &&
        DataValidation.IsValidEmail(value.Email) &&
        value.RegistrationDate.Kind == DateTimeKind.Utc &&
        ValidString(value.RegistrationTimeZoneId, 1, EndpointRpcLimits.MaximumTimeZoneIdLength) &&
        Enum.IsDefined(value.RegistrationDeviceType);

    private static bool ValidLocalDevice(LocalDeviceInfoResponse? value) =>
        value is not null && ValidGuid(value.DeviceId) &&
        ValidString(value.TlsCertFingerprint, 1, EndpointRpcLimits.MaximumFingerprintLength) &&
        Enum.IsDefined(value.DeviceType) && value.CreatedAt.Offset == TimeSpan.Zero;

    private static bool ValidUserDevice(UserDeviceInfoResponse value) =>
        value is not null && ValidGuid(value.DeviceId) &&
        DataValidation.IsValidUserDeviceName(value.Name) && Enum.IsDefined(value.DeviceType) &&
        ValidString(value.TlsCertFingerprint, 1, EndpointRpcLimits.MaximumFingerprintLength) &&
        ValidUtc(value.LastSync) && ValidUtc(value.LastSeen) && ValidUtc(value.LastLoginDate) &&
        ValidUtc(value.PreviousLoginDate) && (!value.BlockedAt.HasValue || value.BlockedAt.Value.Offset == TimeSpan.Zero) &&
        value.InvalidSyncAttemptCount >= 0 && value.LinkedAt.Offset == TimeSpan.Zero &&
        (!value.DeletedAt.HasValue || value.DeletedAt.Value.Offset == TimeSpan.Zero) &&
        (value.BlockedReason is null || value.BlockedReason.Length <= EndpointRpcLimits.MaximumSafeErrorMessageLength);

    private static bool ValidRemovalResult(DeviceRemovalResultResponse? value) =>
        value is not null && value.ResultingMembershipEpoch >= 0 &&
        value.Message is not null && value.Message.Length <= EndpointRpcLimits.MaximumSafeErrorMessageLength &&
        (!value.OperationId.HasValue || value.OperationId.Value != Guid.Empty);

    private static bool ValidEnrollmentCode(DeviceEnrollmentCodeResponse? value) =>
        value is not null && ValidString(value.Code, 1, EndpointRpcLimits.MaximumEnrollmentCodeLength) &&
        value.ExpiresAt.Offset == TimeSpan.Zero;

    private static bool ValidEnrollmentStatus(DeviceEnrollmentStatusResponse? value) =>
        value is not null && Enum.IsDefined(value.State) && Enum.IsDefined(value.ErrorCode) &&
        (value.ErrorMessage is null || value.ErrorMessage.Length <= EndpointRpcLimits.MaximumSafeErrorMessageLength) &&
        (!value.ExpiresAt.HasValue || value.ExpiresAt.Value.Offset == TimeSpan.Zero);

    private static bool ValidSavedPasswords(SavedPasswordsResponse? value) =>
        value is not null &&
        value.Passwords is not null && value.Passwords.Count <= EndpointRpcLimits.MaximumCollectionItems &&
        value.Passwords.All(ValidPasswordInfo) &&
        value.Passwords.Select(password => password.Id).Distinct().Count() == value.Passwords.Count &&
        value.CustomColors is not null && value.CustomColors.Count <= EndpointRpcLimits.MaximumCollectionItems &&
        value.CustomColors.All(ValidColorInfo) &&
        value.CustomColors.Select(color => color.Id).Distinct().Count() == value.CustomColors.Count &&
        value.Tags is not null && value.Tags.Count <= EndpointRpcLimits.MaximumCollectionItems &&
        value.Tags.All(ValidTagInfo) &&
        value.Tags.Select(tag => tag.Id).Distinct().Count() == value.Tags.Count;

    private static bool ValidPasswordInfo(PasswordInfoResponse value) =>
        value is not null && ValidGuid(value.Id) && value.Name is not null && value.Description is not null &&
        value.Color is not null && DataValidation.IsValidPasswordName(value.Name) &&
        DataValidation.IsValidDescription(value.Description) && DataValidation.IsValidARGBColor(value.Color) &&
        ValidIds(value.TagIds, requireNonEmpty: false) && value.CreatedAt.Kind == DateTimeKind.Utc &&
        value.LastUpdatedAt.Kind == DateTimeKind.Utc;

    private static bool ValidColorInfo(CustomUserColorInfoResponse value) =>
        value is not null && ValidGuid(value.Id) && value.ColorCode is not null &&
        DataValidation.IsValidCustomUserColorName(value.ColorName) &&
        DataValidation.IsValidARGBColor(value.ColorCode) && value.LastUpdatedAt.Kind == DateTimeKind.Utc;

    private static bool ValidTagInfo(PasswordTagInfoResponse value) =>
        value is not null && ValidGuid(value.Id) && value.Name is not null && value.Color is not null &&
        DataValidation.IsValidPasswordTagName(value.Name) &&
        DataValidation.IsValidARGBColor(value.Color) && value.LastUpdatedAt.Kind == DateTimeKind.Utc;

    private static bool ValidUtc(DateTime? value) => !value.HasValue || value.Value.Kind == DateTimeKind.Utc;
}
