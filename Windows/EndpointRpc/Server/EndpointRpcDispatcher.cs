using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointRpcDispatcher : IAsyncDisposable
{
    private readonly IEndpointRpcEndpointAdapter _endpointAdapter;
    private readonly EndpointRpcSerializer _serializer;
    private readonly EndpointRpcContractValidator _validator;
    private readonly EndpointRpcBackendErrorMapper _errorMapper;
    private readonly EndpointLargeResultTransferStore _largeResultTransferStore;
    private readonly IEndpointRpcRestartRequirementHandler? _restartRequirementHandler;
    private readonly bool _ownsLargeResultTransferStore;
    private int _disposed;

    internal EndpointLargeResultTransferStore LargeResultTransferStore => _largeResultTransferStore;

    public EndpointRpcDispatcher(
        IEndpointRpcEndpointAdapter endpointAdapter,
        EndpointRpcSerializer serializer,
        EndpointRpcContractValidator validator,
        EndpointRpcBackendErrorMapper errorMapper,
        IEndpointRpcRestartRequirementHandler? restartRequirementHandler = null)
        : this(
            endpointAdapter,
            serializer,
            validator,
            errorMapper,
            new EndpointLargeResultTransferStore(),
            ownsLargeResultTransferStore: true,
            restartRequirementHandler: restartRequirementHandler)
    {
    }

    internal EndpointRpcDispatcher(
        IEndpointRpcEndpointAdapter endpointAdapter,
        EndpointRpcSerializer serializer,
        EndpointRpcContractValidator validator,
        EndpointRpcBackendErrorMapper errorMapper,
        EndpointLargeResultTransferStore largeResultTransferStore,
        IEndpointRpcRestartRequirementHandler? restartRequirementHandler = null)
        : this(
            endpointAdapter,
            serializer,
            validator,
            errorMapper,
            largeResultTransferStore,
            ownsLargeResultTransferStore: false,
            restartRequirementHandler: restartRequirementHandler)
    {
    }

    private EndpointRpcDispatcher(
        IEndpointRpcEndpointAdapter endpointAdapter,
        EndpointRpcSerializer serializer,
        EndpointRpcContractValidator validator,
        EndpointRpcBackendErrorMapper errorMapper,
        EndpointLargeResultTransferStore largeResultTransferStore,
        bool ownsLargeResultTransferStore,
        IEndpointRpcRestartRequirementHandler? restartRequirementHandler)
    {
        _endpointAdapter = endpointAdapter ?? throw new ArgumentNullException(nameof(endpointAdapter));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _largeResultTransferStore = largeResultTransferStore
            ?? throw new ArgumentNullException(nameof(largeResultTransferStore));
        _ownsLargeResultTransferStore = ownsLargeResultTransferStore;
        _restartRequirementHandler = restartRequirementHandler;
    }

    public async Task<EndpointRpcDispatchResult> DispatchAsync(
        EndpointRequestContext context,
        byte[] requestPayload,
        CancellationToken transportCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestPayload);
        if (requestPayload.Length > EndpointRpcLimits.MaximumRequestPayloadSize)
            return Failure(context, EndpointRpcErrorCode.RequestPayloadTooLarge, EndpointRpcErrorCategory.Validation, "The endpoint RPC request payload exceeds the permitted size.");

        EndpointOperationDescriptor descriptor;
        try
        {
            descriptor = EndpointOperationManifest.Get(context.OperationId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure(context, EndpointRpcErrorCode.UnknownOperation, EndpointRpcErrorCategory.Validation, "The endpoint RPC operation is not supported.");
        }

        if (requestPayload.Length > descriptor.MaximumRequestPayloadSize)
        {
            return Failure(
                context,
                EndpointRpcErrorCode.RequestPayloadTooLarge,
                EndpointRpcErrorCategory.Validation,
                "The endpoint RPC request payload exceeds the permitted size.");
        }

        var operationToken = descriptor.CancellationClassification == EndpointOperationCancellationClassification.ReadOnlySafelyCancellable
            ? transportCancellationToken
            : CancellationToken.None;
        context = context with { CancellationToken = operationToken };

        try
        {
            return context.OperationId switch
            {
                EndpointOperationId.Register => await DispatchRegisterAsync(context, requestPayload, operationToken),
                EndpointOperationId.Login => await DispatchLoginAsync(context, requestPayload, operationToken),
                EndpointOperationId.RenewAuthSession => await DispatchRenewAuthSessionAsync(context, requestPayload, operationToken),
                EndpointOperationId.Logout => await DispatchLogoutAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetAuthSessionStatus => await DispatchGetAuthSessionStatusAsync(context, requestPayload, operationToken),
                EndpointOperationId.ChangeMasterPassword => await DispatchChangeMasterPasswordAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetUserProfileInfo => await DispatchGetUserProfileInfoAsync(context, requestPayload, operationToken),
                EndpointOperationId.DeleteUserAccount => await DispatchDeleteUserAccountAsync(context, requestPayload, operationToken),
                EndpointOperationId.ChangeUsername => await DispatchChangeUsernameAsync(context, requestPayload, operationToken),
                EndpointOperationId.UpdateUserProfileInfo => await DispatchUpdateUserProfileInfoAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetLocalDeviceInfo => await DispatchGetLocalDeviceInfoAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetLocalUserSyncOn => await DispatchGetLocalUserSyncOnAsync(context, requestPayload, operationToken),
                EndpointOperationId.SetLocalUserSyncOn => await DispatchSetLocalUserSyncOnAsync(context, requestPayload, operationToken),
                EndpointOperationId.SetLocalDeviceName => await DispatchSetLocalDeviceNameAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetUserDevices => await DispatchGetUserDevicesAsync(context, requestPayload, operationToken),
                EndpointOperationId.SetUserDeviceName => await DispatchSetUserDeviceNameAsync(context, requestPayload, operationToken),
                EndpointOperationId.SetUserDeviceSyncOn => await DispatchSetUserDeviceSyncOnAsync(context, requestPayload, operationToken),
                EndpointOperationId.UnblockUserDevice => await DispatchUnblockUserDeviceAsync(context, requestPayload, operationToken),
                EndpointOperationId.DisconnectUserDevice => await DispatchDisconnectUserDeviceAsync(context, requestPayload, operationToken),
                EndpointOperationId.StartDeviceEnrollment => await DispatchStartDeviceEnrollmentAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetDeviceEnrollmentStatus => await DispatchGetDeviceEnrollmentStatusAsync(context, requestPayload, operationToken),
                EndpointOperationId.CancelDeviceEnrollment => await DispatchCancelDeviceEnrollmentAsync(context, requestPayload, operationToken),
                EndpointOperationId.AddDeviceByCode => await DispatchAddDeviceByCodeAsync(context, requestPayload, operationToken),
                EndpointOperationId.RestoreRememberedSessions => await DispatchRestoreRememberedSessionsAsync(context, requestPayload, operationToken),
                EndpointOperationId.InitializeRememberMeSession => await DispatchInitializeRememberMeSessionAsync(context, requestPayload, operationToken),
                EndpointOperationId.SetRememberMe => await DispatchSetRememberMeAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetSavedPasswords => await DispatchGetSavedPasswordsAsync(context, requestPayload, operationToken),
                EndpointOperationId.AddNewPassword => await DispatchAddNewPasswordAsync(context, requestPayload, operationToken),
                EndpointOperationId.RemovePasswords => await DispatchRemovePasswordsAsync(context, requestPayload, operationToken),
                EndpointOperationId.GetUnsecurePassword => await DispatchGetUnsecurePasswordAsync(context, requestPayload, operationToken),
                EndpointOperationId.UpdatePassword => await DispatchUpdatePasswordAsync(context, requestPayload, operationToken),
                EndpointOperationId.ExportPasswordsToUser => await DispatchExportPasswordsToUserAsync(context, requestPayload, operationToken),
                EndpointOperationId.AddCustomUserColors => await DispatchAddCustomUserColorsAsync(context, requestPayload, operationToken),
                EndpointOperationId.DeleteCustomUserColors => await DispatchDeleteCustomUserColorsAsync(context, requestPayload, operationToken),
                EndpointOperationId.ExportCustomUserColorsToUser => await DispatchExportCustomUserColorsToUserAsync(context, requestPayload, operationToken),
                EndpointOperationId.UpdateCustomUserColor => await DispatchUpdateCustomUserColorAsync(context, requestPayload, operationToken),
                EndpointOperationId.AddPasswordTag => await DispatchAddPasswordTagAsync(context, requestPayload, operationToken),
                EndpointOperationId.DeletePasswordTag => await DispatchDeletePasswordTagAsync(context, requestPayload, operationToken),
                EndpointOperationId.ExportPasswordTagsToUser => await DispatchExportPasswordTagsToUserAsync(context, requestPayload, operationToken),
                EndpointOperationId.UpdatePasswordTag => await DispatchUpdatePasswordTagAsync(context, requestPayload, operationToken),
                _ => Failure(context, EndpointRpcErrorCode.UnknownOperation, EndpointRpcErrorCategory.Validation, "The endpoint RPC operation is not supported.")
            };
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.OperationCancelled, EndpointRpcErrorCategory.Cancellation, "The endpoint operation was cancelled.", true);
        }
        catch (EndpointLargeResultCapacityException)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.RuntimeUnavailable, EndpointRpcErrorCategory.Availability, "The endpoint large-result transfer capacity is unavailable.", true);
        }
        catch (EndpointRpcPayloadException)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.ValidationFailed, EndpointRpcErrorCategory.Validation, "The endpoint RPC payload is invalid.");
        }
        catch (Exception exception)
        {
            NotifyRestartRequirement(exception);
            if (MutationOutcomeCouldBeUnknown(descriptor, context))
            {
                if (_errorMapper.TryMapKnownMutationFailure(exception, context, out var knownFailure))
                    return EndpointRpcDispatchResult.Failure(knownFailure);

                return EndpointRpcDispatchResult.Failure(
                    _errorMapper.CreateOutcomeUnknown(
                        context,
                        _errorMapper.RequiresProcessRestart(exception)));
            }

            return EndpointRpcDispatchResult.Failure(_errorMapper.Map(exception, context));
        }
    }

    private void NotifyRestartRequirement(Exception exception)
    {
        if (_restartRequirementHandler is null ||
            !_errorMapper.RequiresProcessRestart(exception))
        {
            return;
        }

        _restartRequirementHandler.RequireProcessRestart(exception);
    }

    private async Task<EndpointRpcDispatchResult> DispatchRegisterAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.RegisterEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.RegisterAsync(request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new RegisterEndpointResponse { Token = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.RegisterEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchLoginAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.LoginEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.LoginAsync(request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new LoginEndpointResponse { Token = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.LoginEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchRenewAuthSessionAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.RenewAuthSessionEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.RenewAuthSessionAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new RenewAuthSessionEndpointResponse { RenewedToken = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.RenewAuthSessionEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchLogoutAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.LogoutEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.LogoutAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new LogoutEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.LogoutEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetAuthSessionStatusAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetAuthSessionStatusAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetAuthSessionStatusEndpointResponse { Status = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchChangeMasterPasswordAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.ChangeMasterPasswordEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.ChangeMasterPasswordAsync(request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new ChangeMasterPasswordEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.ChangeMasterPasswordEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetUserProfileInfoAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetUserProfileInfoAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetUserProfileInfoEndpointResponse { Profile = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchDeleteUserAccountAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.DeleteUserAccountEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.DeleteUserAccountAsync(request.Token, request.Password, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new DeleteUserAccountEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.DeleteUserAccountEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchChangeUsernameAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.ChangeUsernameEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.ChangeUsernameAsync(request.Token, request.NewUsername, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new ChangeUsernameEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.ChangeUsernameEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchUpdateUserProfileInfoAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.UpdateUserProfileInfoEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.UpdateUserProfileInfoAsync(request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new UpdateUserProfileInfoEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.UpdateUserProfileInfoEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetLocalDeviceInfoAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetLocalDeviceInfoAsync(operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetLocalDeviceInfoEndpointResponse { Device = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetLocalUserSyncOnAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetLocalUserSyncOnAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetLocalUserSyncOnEndpointResponse { IsSyncOn = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchSetLocalUserSyncOnAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.SetLocalUserSyncOnEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.SetLocalUserSyncOnAsync(request.Token, request.IsSyncOn, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new SetLocalUserSyncOnEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.SetLocalUserSyncOnEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchSetLocalDeviceNameAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.SetLocalDeviceNameEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.SetLocalDeviceNameAsync(request.Token, request.Name, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new SetLocalDeviceNameEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.SetLocalDeviceNameEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetUserDevicesAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetUserDevicesEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetUserDevicesAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetUserDevicesEndpointResponse { Devices = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetUserDevicesEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchSetUserDeviceNameAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.SetUserDeviceNameEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.SetUserDeviceNameAsync(request.Token, request.DeviceId, request.Name, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new SetUserDeviceNameEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.SetUserDeviceNameEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchSetUserDeviceSyncOnAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.SetUserDeviceSyncOnEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.SetUserDeviceSyncOnAsync(request.Token, request.DeviceId, request.IsSyncOn, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new SetUserDeviceSyncOnEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.SetUserDeviceSyncOnEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchUnblockUserDeviceAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.UnblockUserDeviceEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.UnblockUserDeviceAsync(request.Token, request.DeviceId, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new UnblockUserDeviceEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.UnblockUserDeviceEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchDisconnectUserDeviceAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.DisconnectUserDeviceAsync(request.Token, request.DeviceId, request.MasterPassword, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new DisconnectUserDeviceEndpointResponse { Result = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchStartDeviceEnrollmentAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.StartDeviceEnrollmentEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.StartDeviceEnrollmentAsync(operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new StartDeviceEnrollmentEndpointResponse { Enrollment = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.StartDeviceEnrollmentEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetDeviceEnrollmentStatusAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetDeviceEnrollmentStatusEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetDeviceEnrollmentStatusAsync(operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetDeviceEnrollmentStatusEndpointResponse { Status = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetDeviceEnrollmentStatusEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchCancelDeviceEnrollmentAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.CancelDeviceEnrollmentEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.CancelDeviceEnrollmentAsync(operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new CancelDeviceEnrollmentEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.CancelDeviceEnrollmentEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchAddDeviceByCodeAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.AddDeviceByCodeEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.AddDeviceByCodeAsync(request.Token, request.Code, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new AddDeviceByCodeEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.AddDeviceByCodeEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchRestoreRememberedSessionsAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.RestoreRememberedSessionsEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.RestoreRememberedSessionsAsync(operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new RestoreRememberedSessionsEndpointResponse { Tokens = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.RestoreRememberedSessionsEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchInitializeRememberMeSessionAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.InitializeRememberMeSessionAsync(request.UserId, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new InitializeRememberMeSessionEndpointResponse { Token = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchSetRememberMeAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.SetRememberMeEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.SetRememberMeAsync(request.Token, request.RememberMe, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new SetRememberMeEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.SetRememberMeEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetSavedPasswordsAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetSavedPasswordsAsync(request.Token, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetSavedPasswordsEndpointResponse { Passwords = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchAddNewPasswordAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.AddNewPasswordEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.AddNewPasswordAsync(request.Token, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new AddNewPasswordEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.AddNewPasswordEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchRemovePasswordsAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.RemovePasswordsEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.RemovePasswordsAsync(request.Token, request.PasswordIds, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new RemovePasswordsEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.RemovePasswordsEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchGetUnsecurePasswordAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            var result = await endpoints.GetUnsecurePasswordAsync(request.Token, request.PasswordId, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new GetUnsecurePasswordEndpointResponse { Password = result };
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchUpdatePasswordAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.UpdatePasswordEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.UpdatePasswordAsync(request.Token, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new UpdatePasswordEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.UpdatePasswordEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchExportPasswordsToUserAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.ExportPasswordsToUserEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.ExportPasswordsToUserAsync(request.SourceToken, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new ExportPasswordsToUserEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.ExportPasswordsToUserEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchAddCustomUserColorsAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.AddCustomUserColorsEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.AddCustomUserColorsAsync(request.Token, request.Requests, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new AddCustomUserColorsEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.AddCustomUserColorsEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchDeleteCustomUserColorsAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.DeleteCustomUserColorsEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.DeleteCustomUserColorsAsync(request.Token, request.CustomUserColorIds, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new DeleteCustomUserColorsEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.DeleteCustomUserColorsEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchExportCustomUserColorsToUserAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.ExportCustomUserColorsToUserEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.ExportCustomUserColorsToUserAsync(request.SourceToken, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new ExportCustomUserColorsToUserEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.ExportCustomUserColorsToUserEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchUpdateCustomUserColorAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.UpdateCustomUserColorEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.UpdateCustomUserColorAsync(request.Token, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new UpdateCustomUserColorEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.UpdateCustomUserColorEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchAddPasswordTagAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.AddPasswordTagEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.AddPasswordTagAsync(request.Token, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new AddPasswordTagEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.AddPasswordTagEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchDeletePasswordTagAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.DeletePasswordTagEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.DeletePasswordTagAsync(request.Token, request.PasswordTagId, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new DeletePasswordTagEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.DeletePasswordTagEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchExportPasswordTagsToUserAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.ExportPasswordTagsToUserEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.ExportPasswordTagsToUserAsync(request.SourceToken, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new ExportPasswordTagsToUserEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.ExportPasswordTagsToUserEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private async Task<EndpointRpcDispatchResult> DispatchUpdatePasswordTagAsync(
        EndpointRequestContext context,
        byte[] payload,
        CancellationToken operationToken)
    {
        var request = DeserializeRequest(
            context.OperationId,
            payload,
            EndpointRpcJsonContext.Default.UpdatePasswordTagEndpointRequest);
        try
        {
            var endpoints = _endpointAdapter.GetEndpoints(context);
            context.Invocation.MarkInvoking();
            await endpoints.UpdatePasswordTagAsync(request.Token, request.Request, operationToken);
            context.Invocation.MarkInvocationCompleted();
            var response = new UpdatePasswordTagEndpointResponse();
            return SerializeResponse(
                context,
                response,
                EndpointRpcJsonContext.Default.UpdatePasswordTagEndpointResponse);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsLargeResultTransferStore)
            await _largeResultTransferStore.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private TRequest DeserializeRequest<TRequest>(
        EndpointOperationId operationId,
        byte[] payload,
        JsonTypeInfo<TRequest> typeInfo)
        where TRequest : class
    {
        var request = _serializer.Deserialize(payload, typeInfo);
        try
        {
            _validator.ValidateRequest(operationId, request);
            return request;
        }
        catch
        {
            EndpointSensitiveData.ClearRequest(operationId, request);
            throw;
        }
    }

    private EndpointRpcDispatchResult SerializeResponse<TResponse>(
        EndpointRequestContext context,
        TResponse response,
        JsonTypeInfo<TResponse> typeInfo)
        where TResponse : class
    {
        byte[]? payload = null;
        try
        {
            _validator.ValidateResponse(context.OperationId, response);
            context.Invocation.MarkResponseValidated();
            payload = _serializer.Serialize(response, typeInfo);
            context.Invocation.MarkResponseSerialized();
            var descriptor = EndpointOperationManifest.Get(context.OperationId);
            if (payload.Length > descriptor.MaximumLogicalResponsePayloadSize)
            {
                EndpointSensitiveData.Clear(payload);
                payload = null;
                return descriptor.MutatesState
                    ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                    : Failure(
                        context,
                        EndpointRpcErrorCode.ResponsePayloadTooLarge,
                        EndpointRpcErrorCategory.Validation,
                        "The endpoint RPC response payload exceeds the permitted size.");
            }

            if (payload.Length > descriptor.MaximumResponsePayloadSize)
            {
                if (!descriptor.SupportsLargeResponse)
                {
                    EndpointSensitiveData.Clear(payload);
                    payload = null;
                    return descriptor.MutatesState
                        ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                        : Failure(
                            context,
                            EndpointRpcErrorCode.ResponsePayloadTooLarge,
                            EndpointRpcErrorCategory.Validation,
                            "The endpoint RPC response payload exceeds the permitted size.");
                }

                var largeResult = _largeResultTransferStore.Create(context, payload);
                payload = null;
                return EndpointRpcDispatchResult.Large(largeResult);
            }

            var inlinePayload = payload;
            payload = null;
            return EndpointRpcDispatchResult.Success(inlinePayload);
        }
        finally
        {
            EndpointSensitiveData.Clear(payload);
            EndpointSensitiveData.ClearResponse(context.OperationId, response);
        }
    }

    private static bool MutationOutcomeCouldBeUnknown(
        EndpointOperationDescriptor descriptor,
        EndpointRequestContext context) =>
        descriptor.MutatesState &&
        context.Invocation.Stage >= EndpointInvocationStage.Invoking;

    private static EndpointRpcDispatchResult Failure(
        EndpointRequestContext context,
        EndpointRpcErrorCode code,
        EndpointRpcErrorCategory category,
        string safeMessage,
        bool isRetryable = false) =>
        EndpointRpcDispatchResult.Failure(
            new EndpointRpcError(
                code,
                category,
                safeMessage,
                context.CorrelationId,
                DateTimeOffset.UtcNow,
                isRetryable,
                RequiresProcessRestart: false,
                GetConclusiveMutationOutcome(context.OperationId),
                Recovery: null));
    private static EndpointMutationOutcome GetConclusiveMutationOutcome(EndpointOperationId operationId) =>
        Enum.IsDefined(operationId) && EndpointOperationManifest.Get(operationId).MutatesState
            ? EndpointMutationOutcome.NotCommitted
            : EndpointMutationOutcome.NotApplicable;

}
