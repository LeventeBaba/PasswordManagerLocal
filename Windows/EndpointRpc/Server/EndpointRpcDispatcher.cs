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
            return Failure(context, EndpointRpcErrorCode.RequestPayloadTooLarge, EndpointFailureKind.InvalidInput, "The endpoint RPC request payload exceeds the permitted size.");

        EndpointOperationDescriptor descriptor;
        try
        {
            descriptor = EndpointOperationManifest.Get(context.OperationId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure(context, EndpointRpcErrorCode.UnknownOperation, EndpointFailureKind.Generic, "The endpoint RPC operation is not supported.");
        }

        if (requestPayload.Length > descriptor.MaximumRequestPayloadSize)
        {
            return Failure(
                context,
                EndpointRpcErrorCode.RequestPayloadTooLarge,
                EndpointFailureKind.InvalidInput,
                "The endpoint RPC request payload exceeds the permitted size.");
        }

        var operationToken = !descriptor.MutatesState
            ? transportCancellationToken
            : CancellationToken.None;
        context = context with { CancellationToken = operationToken };

        try
        {
            return context.OperationId switch
            {
                EndpointOperationId.Register => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.RegisterEndpointRequest,
                    EndpointRpcJsonContext.Default.RegisterEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.RegisterAsync(request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new RegisterEndpointResponse { Token = result };
                    }),
                EndpointOperationId.Login => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.LoginEndpointRequest,
                    EndpointRpcJsonContext.Default.LoginEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.LoginAsync(request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new LoginEndpointResponse { Token = result };
                    }),
                EndpointOperationId.RenewAuthSession => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.RenewAuthSessionEndpointRequest,
                    EndpointRpcJsonContext.Default.RenewAuthSessionEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.RenewAuthSessionAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new RenewAuthSessionEndpointResponse { RenewedToken = result };
                    }),
                EndpointOperationId.Logout => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.LogoutEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.LogoutAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetAuthSessionStatus => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointRequest,
                    EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetAuthSessionStatusAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetAuthSessionStatusEndpointResponse { Status = result };
                    }),
                EndpointOperationId.ChangeMasterPassword => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.ChangeMasterPasswordEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.ChangeMasterPasswordAsync(request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetUserProfileInfo => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointRequest,
                    EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetUserProfileInfoAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetUserProfileInfoEndpointResponse { Profile = result };
                    }),
                EndpointOperationId.DeleteUserAccount => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.DeleteUserAccountEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.DeleteUserAccountAsync(request.Token, request.Password, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.ChangeUsername => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.ChangeUsernameEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.ChangeUsernameAsync(request.Token, request.NewUsername, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.UpdateUserProfileInfo => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.UpdateUserProfileInfoEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.UpdateUserProfileInfoAsync(request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetLocalDeviceInfo => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.EmptyEndpointRequest,
                    EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetLocalDeviceInfoAsync(token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetLocalDeviceInfoEndpointResponse { Device = result };
                    }),
                EndpointOperationId.GetLocalUserSyncOn => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointRequest,
                    EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetLocalUserSyncOnAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetLocalUserSyncOnEndpointResponse { IsSyncOn = result };
                    }),
                EndpointOperationId.SetLocalUserSyncOn => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.SetLocalUserSyncOnEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.SetLocalUserSyncOnAsync(request.Token, request.IsSyncOn, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.SetLocalDeviceName => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.SetLocalDeviceNameEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.SetLocalDeviceNameAsync(request.Token, request.Name, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetUserDevices => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetUserDevicesEndpointRequest,
                    EndpointRpcJsonContext.Default.GetUserDevicesEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetUserDevicesAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetUserDevicesEndpointResponse { Devices = result };
                    }),
                EndpointOperationId.RunSelfDiagnosticsAndRepair => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.RunSelfDiagnosticsAndRepairEndpointRequest,
                    EndpointRpcJsonContext.Default.RunSelfDiagnosticsAndRepairEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.RunSelfDiagnosticsAndRepairAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new RunSelfDiagnosticsAndRepairEndpointResponse { Result = result };
                    }),
                EndpointOperationId.SetUserDeviceName => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.SetUserDeviceNameEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.SetUserDeviceNameAsync(request.Token, request.DeviceId, request.Name, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.SetUserDeviceSyncOn => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.SetUserDeviceSyncOnEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.SetUserDeviceSyncOnAsync(request.Token, request.DeviceId, request.IsSyncOn, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.UnblockUserDevice => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.UnblockUserDeviceEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.UnblockUserDeviceAsync(request.Token, request.DeviceId, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.DisconnectUserDevice => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointRequest,
                    EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.DisconnectUserDeviceAsync(request.Token, request.DeviceId, request.MasterPassword, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new DisconnectUserDeviceEndpointResponse { Result = result };
                    }),
                EndpointOperationId.StartDeviceEnrollment => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.EmptyEndpointRequest,
                    EndpointRpcJsonContext.Default.StartDeviceEnrollmentEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.StartDeviceEnrollmentAsync(token);
                        context.Invocation.MarkInvocationCompleted();
                        return new StartDeviceEnrollmentEndpointResponse { Enrollment = result };
                    }),
                EndpointOperationId.GetDeviceEnrollmentStatus => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.EmptyEndpointRequest,
                    EndpointRpcJsonContext.Default.GetDeviceEnrollmentStatusEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetDeviceEnrollmentStatusAsync(token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetDeviceEnrollmentStatusEndpointResponse { Status = result };
                    }),
                EndpointOperationId.CancelDeviceEnrollment => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.EmptyEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.CancelDeviceEnrollmentAsync(token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.AddDeviceByCode => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.AddDeviceByCodeEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.AddDeviceByCodeAsync(request.Token, request.Code, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.RestoreRememberedSessions => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.EmptyEndpointRequest,
                    EndpointRpcJsonContext.Default.RestoreRememberedSessionsEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.RestoreRememberedSessionsAsync(token);
                        context.Invocation.MarkInvocationCompleted();
                        return new RestoreRememberedSessionsEndpointResponse { Tokens = result };
                    }),
                EndpointOperationId.InitializeRememberMeSession => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointRequest,
                    EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.InitializeRememberMeSessionAsync(request.UserId, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new InitializeRememberMeSessionEndpointResponse { Token = result };
                    }),
                EndpointOperationId.SetRememberMe => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.SetRememberMeEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.SetRememberMeAsync(request.Token, request.RememberMe, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetSavedPasswords => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest,
                    EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetSavedPasswordsAsync(request.Token, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetSavedPasswordsEndpointResponse { Passwords = result };
                    }),
                EndpointOperationId.AddNewPassword => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.AddNewPasswordEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.AddNewPasswordAsync(request.Token, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.RemovePasswords => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.RemovePasswordsEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.RemovePasswordsAsync(request.Token, request.PasswordIds, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.GetUnsecurePassword => await DispatchAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointRequest,
                    EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointResponse,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        var result = await endpoints.GetUnsecurePasswordAsync(request.Token, request.PasswordId, token);
                        context.Invocation.MarkInvocationCompleted();
                        return new GetUnsecurePasswordEndpointResponse { Password = result };
                    }),
                EndpointOperationId.UpdatePassword => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.UpdatePasswordEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.UpdatePasswordAsync(request.Token, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.ExportPasswordsToUser => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.ExportPasswordsToUserEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.ExportPasswordsToUserAsync(request.SourceToken, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.AddCustomUserColors => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.AddCustomUserColorsEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.AddCustomUserColorsAsync(request.Token, request.Requests, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.DeleteCustomUserColors => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.DeleteCustomUserColorsEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.DeleteCustomUserColorsAsync(request.Token, request.CustomUserColorIds, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.ExportCustomUserColorsToUser => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.ExportCustomUserColorsToUserEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.ExportCustomUserColorsToUserAsync(request.SourceToken, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.UpdateCustomUserColor => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.UpdateCustomUserColorEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.UpdateCustomUserColorAsync(request.Token, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.AddPasswordTag => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.AddPasswordTagEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.AddPasswordTagAsync(request.Token, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.DeletePasswordTag => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.DeletePasswordTagEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.DeletePasswordTagAsync(request.Token, request.PasswordTagId, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.ExportPasswordTagsToUser => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.ExportPasswordTagsToUserEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.ExportPasswordTagsToUserAsync(request.SourceToken, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                EndpointOperationId.UpdatePasswordTag => await DispatchVoidAsync(
                    context,
                    requestPayload,
                    EndpointRpcJsonContext.Default.UpdatePasswordTagEndpointRequest,
                    async (request, token) =>
                    {
                        var endpoints = _endpointAdapter.GetEndpoints(context);
                        context.Invocation.MarkInvoking();
                        await endpoints.UpdatePasswordTagAsync(request.Token, request.Request, token);
                        context.Invocation.MarkInvocationCompleted();
                    }),
                _ => Failure(context, EndpointRpcErrorCode.UnknownOperation, EndpointFailureKind.Generic, "The endpoint RPC operation is not supported.")
            };
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.OperationCancelled, EndpointFailureKind.Generic, "The endpoint operation was cancelled.");
        }
        catch (EndpointLargeResultCapacityException)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.RuntimeUnavailable, EndpointFailureKind.Generic, "The endpoint large-result transfer capacity is unavailable.");
        }
        catch (EndpointRpcPayloadException)
        {
            return MutationOutcomeCouldBeUnknown(descriptor, context)
                ? EndpointRpcDispatchResult.Failure(_errorMapper.CreateOutcomeUnknown(context))
                : Failure(context, EndpointRpcErrorCode.ValidationFailed, EndpointFailureKind.InvalidInput, "The endpoint RPC payload is invalid.");
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

    private async Task<EndpointRpcDispatchResult> DispatchAsync<TRequest, TResponse>(
        EndpointRequestContext context,
        byte[] payload,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Func<TRequest, CancellationToken, Task<TResponse>> invoke)
        where TRequest : class
        where TResponse : class
    {
        var request = DeserializeRequest(context.OperationId, payload, requestTypeInfo);
        try
        {
            var response = await invoke(request, context.CancellationToken);
            return SerializeResponse(context, response, responseTypeInfo);
        }
        finally
        {
            EndpointSensitiveData.ClearRequest(context.OperationId, request);
        }
    }

    private Task<EndpointRpcDispatchResult> DispatchVoidAsync<TRequest>(
        EndpointRequestContext context,
        byte[] payload,
        JsonTypeInfo<TRequest> requestTypeInfo,
        Func<TRequest, CancellationToken, Task> invoke)
        where TRequest : class =>
        DispatchAsync(
            context,
            payload,
            requestTypeInfo,
            EndpointRpcJsonContext.Default.EmptyEndpointResponse,
            async (request, cancellationToken) =>
            {
                await invoke(request, cancellationToken);
                return new EmptyEndpointResponse();
            });

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
                        EndpointFailureKind.Generic,
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
                            EndpointFailureKind.Generic,
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
        EndpointFailureKind failureKind,
        string safeMessage) =>
        EndpointRpcDispatchResult.Failure(
            new EndpointRpcError(
                code,
                failureKind,
                safeMessage,
                RequiresProcessRestart: false,
                GetConclusiveMutationOutcome(context.OperationId)));
    private static EndpointMutationOutcome GetConclusiveMutationOutcome(EndpointOperationId operationId) =>
        Enum.IsDefined(operationId) && EndpointOperationManifest.Get(operationId).MutatesState
            ? EndpointMutationOutcome.NotCommitted
            : EndpointMutationOutcome.NotApplicable;

}
