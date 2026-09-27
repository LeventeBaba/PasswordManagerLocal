using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class NamedPipeEndpointsProxy : IEndpoints, IAsyncDisposable
{
    private readonly IEndpointRpcTransport _transport;
    private readonly EndpointRpcSerializer _serializer;
    private readonly EndpointRpcContractValidator _validator;
    private readonly EndpointLargeResultContractValidator _largeResultValidator = new();
    private readonly EndpointRpcClientOptions _options;
    private readonly SemaphoreSlim _operationCapacity;
    private readonly CancellationTokenSource _disposeSource = new();
    private int _disposed;

    public NamedPipeEndpointsProxy(
        IEndpointRpcTransport transport,
        EndpointRpcSerializer serializer,
        EndpointRpcContractValidator validator)
        : this(transport, serializer, validator, new EndpointRpcClientOptions())
    {
    }

    public NamedPipeEndpointsProxy(
        IEndpointRpcTransport transport,
        EndpointRpcSerializer serializer,
        EndpointRpcContractValidator validator,
        EndpointRpcClientOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _operationCapacity = new SemaphoreSlim(
            options.MaximumConcurrentOperations,
            options.MaximumConcurrentOperations);
    }

    public async Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.Register,
            () => new RegisterEndpointRequest { Request = EndpointSensitiveData.Clone(request) },
            EndpointRpcJsonContext.Default.RegisterEndpointRequest,
            EndpointRpcJsonContext.Default.RegisterEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawString(request.Username, DataLengthConstants.UsernameMinLength, DataLengthConstants.UsernameMaxLength) &&
                ValidRawBytes(request.Password) &&
                ValidRawString(request.FirstName, DataLengthConstants.FirstNameMinLength, DataLengthConstants.FirstNameMaxLength) &&
                ValidRawString(request.LastName, DataLengthConstants.LastNameMinLength, DataLengthConstants.LastNameMaxLength) &&
                ValidRawString(request.Email, DataLengthConstants.EmailMinLength, DataLengthConstants.EmailMaxLength)));
        return response.Token;
    }

    public async Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.Login,
            () => new LoginEndpointRequest { Request = EndpointSensitiveData.Clone(request) },
            EndpointRpcJsonContext.Default.LoginEndpointRequest,
            EndpointRpcJsonContext.Default.LoginEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawString(request.Username, DataLengthConstants.UsernameMinLength, DataLengthConstants.UsernameMaxLength) &&
                ValidRawBytes(request.Password)));
        return response.Token;
    }

    public async Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.RenewAuthSession,
            () => new RenewAuthSessionEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.RenewAuthSessionEndpointRequest,
            EndpointRpcJsonContext.Default.RenewAuthSessionEndpointResponse,
            ct);
        return response.RenewedToken;
    }

    public async Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.Logout,
            () => new LogoutEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.LogoutEndpointRequest,
            EndpointRpcJsonContext.Default.LogoutEndpointResponse,
            ct);
    }

    public async Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetAuthSessionStatus,
            () => new GetAuthSessionStatusEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointRequest,
            EndpointRpcJsonContext.Default.GetAuthSessionStatusEndpointResponse,
            ct);
        return response.Status;
    }

    public async Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.ChangeMasterPassword,
            () => new ChangeMasterPasswordEndpointRequest { Request = EndpointSensitiveData.Clone(request) },
            EndpointRpcJsonContext.Default.ChangeMasterPasswordEndpointRequest,
            EndpointRpcJsonContext.Default.ChangeMasterPasswordEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawBytes(request.Password) &&
                ValidRawBytes(request.NewPassword)));
    }

    public async Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetUserProfileInfo,
            () => new GetUserProfileInfoEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointRequest,
            EndpointRpcJsonContext.Default.GetUserProfileInfoEndpointResponse,
            ct);
        return response.Profile;
    }

    public async Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.DeleteUserAccount,
            () => new DeleteUserAccountEndpointRequest { Token = token, Password = password.ToArray() },
            EndpointRpcJsonContext.Default.DeleteUserAccountEndpointRequest,
            EndpointRpcJsonContext.Default.DeleteUserAccountEndpointResponse,
            ct,
            () => EnsureRawInput(ValidRawBytes(password)));
    }

    public async Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.ChangeUsername,
            () => new ChangeUsernameEndpointRequest { Token = token, NewUsername = newUsername },
            EndpointRpcJsonContext.Default.ChangeUsernameEndpointRequest,
            EndpointRpcJsonContext.Default.ChangeUsernameEndpointResponse,
            ct,
            () => EnsureRawInput(
                ValidRawString(newUsername, DataLengthConstants.UsernameMinLength, DataLengthConstants.UsernameMaxLength)));
    }

    public async Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.UpdateUserProfileInfo,
            () => new UpdateUserProfileInfoEndpointRequest { Request = request },
            EndpointRpcJsonContext.Default.UpdateUserProfileInfoEndpointRequest,
            EndpointRpcJsonContext.Default.UpdateUserProfileInfoEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidOptionalRawString(request.NewEamil, DataLengthConstants.EmailMinLength, DataLengthConstants.EmailMaxLength) &&
                ValidOptionalRawString(request.newFirstName, DataLengthConstants.FirstNameMinLength, DataLengthConstants.FirstNameMaxLength) &&
                ValidOptionalRawString(request.NewLastName, DataLengthConstants.LastNameMinLength, DataLengthConstants.LastNameMaxLength)));
    }

    public async Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetLocalDeviceInfo,
            () => new GetLocalDeviceInfoEndpointRequest(),
            EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointRequest,
            EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointResponse,
            ct);
        return response.Device;
    }

    public async Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetLocalUserSyncOn,
            () => new GetLocalUserSyncOnEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointRequest,
            EndpointRpcJsonContext.Default.GetLocalUserSyncOnEndpointResponse,
            ct);
        return response.IsSyncOn;
    }

    public async Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.SetLocalUserSyncOn,
            () => new SetLocalUserSyncOnEndpointRequest { Token = token, IsSyncOn = isSyncOn },
            EndpointRpcJsonContext.Default.SetLocalUserSyncOnEndpointRequest,
            EndpointRpcJsonContext.Default.SetLocalUserSyncOnEndpointResponse,
            ct);
    }

    public async Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.SetLocalDeviceName,
            () => new SetLocalDeviceNameEndpointRequest { Token = token, Name = name },
            EndpointRpcJsonContext.Default.SetLocalDeviceNameEndpointRequest,
            EndpointRpcJsonContext.Default.SetLocalDeviceNameEndpointResponse,
            ct,
            () => EnsureRawInput(
                ValidRawString(name, DataLengthConstants.UserDeviceNameMinLength, DataLengthConstants.UserDeviceNameMaxLength)));
    }

    public async Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetUserDevices,
            () => new GetUserDevicesEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.GetUserDevicesEndpointRequest,
            EndpointRpcJsonContext.Default.GetUserDevicesEndpointResponse,
            ct);
        return response.Devices;
    }

    public async Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.SetUserDeviceName,
            () => new SetUserDeviceNameEndpointRequest { Token = token, DeviceId = deviceId, Name = name },
            EndpointRpcJsonContext.Default.SetUserDeviceNameEndpointRequest,
            EndpointRpcJsonContext.Default.SetUserDeviceNameEndpointResponse,
            ct,
            () => EnsureRawInput(
                ValidRawString(name, DataLengthConstants.UserDeviceNameMinLength, DataLengthConstants.UserDeviceNameMaxLength)));
    }

    public async Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.SetUserDeviceSyncOn,
            () => new SetUserDeviceSyncOnEndpointRequest { Token = token, DeviceId = deviceId, IsSyncOn = isSyncOn },
            EndpointRpcJsonContext.Default.SetUserDeviceSyncOnEndpointRequest,
            EndpointRpcJsonContext.Default.SetUserDeviceSyncOnEndpointResponse,
            ct);
    }

    public async Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.UnblockUserDevice,
            () => new UnblockUserDeviceEndpointRequest { Token = token, DeviceId = deviceId },
            EndpointRpcJsonContext.Default.UnblockUserDeviceEndpointRequest,
            EndpointRpcJsonContext.Default.UnblockUserDeviceEndpointResponse,
            ct);
    }

    public async Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.DisconnectUserDevice,
            () => new DisconnectUserDeviceEndpointRequest { Token = token, DeviceId = deviceId, MasterPassword = masterPassword.ToArray() },
            EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointRequest,
            EndpointRpcJsonContext.Default.DisconnectUserDeviceEndpointResponse,
            ct,
            () => EnsureRawInput(ValidRawBytes(masterPassword)));
        return response.Result;
    }

    public async Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.StartDeviceEnrollment,
            () => new StartDeviceEnrollmentEndpointRequest(),
            EndpointRpcJsonContext.Default.StartDeviceEnrollmentEndpointRequest,
            EndpointRpcJsonContext.Default.StartDeviceEnrollmentEndpointResponse,
            ct);
        return response.Enrollment;
    }

    public async Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetDeviceEnrollmentStatus,
            () => new GetDeviceEnrollmentStatusEndpointRequest(),
            EndpointRpcJsonContext.Default.GetDeviceEnrollmentStatusEndpointRequest,
            EndpointRpcJsonContext.Default.GetDeviceEnrollmentStatusEndpointResponse,
            ct);
        return response.Status;
    }

    public async Task CancelDeviceEnrollmentAsync(CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.CancelDeviceEnrollment,
            () => new CancelDeviceEnrollmentEndpointRequest(),
            EndpointRpcJsonContext.Default.CancelDeviceEnrollmentEndpointRequest,
            EndpointRpcJsonContext.Default.CancelDeviceEnrollmentEndpointResponse,
            ct);
    }

    public async Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.AddDeviceByCode,
            () => new AddDeviceByCodeEndpointRequest { Token = token, Code = code },
            EndpointRpcJsonContext.Default.AddDeviceByCodeEndpointRequest,
            EndpointRpcJsonContext.Default.AddDeviceByCodeEndpointResponse,
            ct,
            () => EnsureRawInput(
                ValidRawString(code, 1, EndpointRpcLimits.MaximumEnrollmentCodeLength)));
    }

    public async Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.RestoreRememberedSessions,
            () => new RestoreRememberedSessionsEndpointRequest(),
            EndpointRpcJsonContext.Default.RestoreRememberedSessionsEndpointRequest,
            EndpointRpcJsonContext.Default.RestoreRememberedSessionsEndpointResponse,
            ct);
        return response.Tokens;
    }

    public async Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.InitializeRememberMeSession,
            () => new InitializeRememberMeSessionEndpointRequest { UserId = userId },
            EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointRequest,
            EndpointRpcJsonContext.Default.InitializeRememberMeSessionEndpointResponse,
            ct);
        return response.Token;
    }

    public async Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.SetRememberMe,
            () => new SetRememberMeEndpointRequest { Token = token, RememberMe = rememberMe },
            EndpointRpcJsonContext.Default.SetRememberMeEndpointRequest,
            EndpointRpcJsonContext.Default.SetRememberMeEndpointResponse,
            ct);
    }

    public async Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetSavedPasswords,
            () => new GetSavedPasswordsEndpointRequest { Token = token },
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest,
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse,
            ct);
        return response.Passwords;
    }

    public async Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.AddNewPassword,
            () => new AddNewPasswordEndpointRequest { Token = token, Request = EndpointSensitiveData.Clone(request) },
            EndpointRpcJsonContext.Default.AddNewPasswordEndpointRequest,
            EndpointRpcJsonContext.Default.AddNewPasswordEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawString(request.Name, DataLengthConstants.PasswordNameMinLength, DataLengthConstants.PasswordNameMaxLength) &&
                ValidRawString(request.Description, DataLengthConstants.DescriptionMinLength, DataLengthConstants.DescriptionMaxLength) &&
                ValidExactRawString(request.Color, DataLengthConstants.ARGBColorLength) &&
                ValidRawBytes(request.Password) &&
                ValidRawCollectionCount(request.TagIds, requireNonEmpty: false)));
    }

    public async Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.RemovePasswords,
            () => new RemovePasswordsEndpointRequest { Token = token, PasswordIds = passwordIds.ToArray() },
            EndpointRpcJsonContext.Default.RemovePasswordsEndpointRequest,
            EndpointRpcJsonContext.Default.RemovePasswordsEndpointResponse,
            ct,
            () => EnsureRawInput(ValidRawCollectionCount(passwordIds, requireNonEmpty: true)));
    }

    public async Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default)
    {
        var response = await InvokeAsync(
            EndpointOperationId.GetUnsecurePassword,
            () => new GetUnsecurePasswordEndpointRequest { Token = token, PasswordId = passwordId },
            EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointRequest,
            EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointResponse,
            ct);
        return response.Password;
    }

    public async Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.UpdatePassword,
            () => new UpdatePasswordEndpointRequest { Token = token, Request = EndpointSensitiveData.Clone(request) },
            EndpointRpcJsonContext.Default.UpdatePasswordEndpointRequest,
            EndpointRpcJsonContext.Default.UpdatePasswordEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidOptionalRawString(request.Name, DataLengthConstants.PasswordNameMinLength, DataLengthConstants.PasswordNameMaxLength) &&
                ValidOptionalRawString(request.Description, DataLengthConstants.DescriptionMinLength, DataLengthConstants.DescriptionMaxLength) &&
                ValidOptionalExactRawString(request.Color, DataLengthConstants.ARGBColorLength) &&
                ValidOptionalRawBytes(request.Password) &&
                ValidOptionalRawCollectionCount(request.TagIds)));
    }

    public async Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.ExportPasswordsToUser,
            () => new ExportPasswordsToUserEndpointRequest { SourceToken = sourceToken, Request = request },
            EndpointRpcJsonContext.Default.ExportPasswordsToUserEndpointRequest,
            EndpointRpcJsonContext.Default.ExportPasswordsToUserEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawCollectionCount(request.PasswordIds, requireNonEmpty: true)));
    }

    public async Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.AddCustomUserColors,
            () => new AddCustomUserColorsEndpointRequest { Token = token, Requests = requests.ToArray() },
            EndpointRpcJsonContext.Default.AddCustomUserColorsEndpointRequest,
            EndpointRpcJsonContext.Default.AddCustomUserColorsEndpointResponse,
            ct,
            () => EnsureRawInput(requests is not null &&
                requests.Count > 0 &&
                requests.Count <= EndpointRpcLimits.MaximumCollectionItems &&
                requests.All(value => value is not null &&
                    ValidOptionalRawString(value.ColorName, 1, DataLengthConstants.CustomUserColorNameMaxLength) &&
                    ValidExactRawString(value.ColorCode, DataLengthConstants.ARGBColorLength))));
    }

    public async Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.DeleteCustomUserColors,
            () => new DeleteCustomUserColorsEndpointRequest { Token = token, CustomUserColorIds = customUserColorIds.ToArray() },
            EndpointRpcJsonContext.Default.DeleteCustomUserColorsEndpointRequest,
            EndpointRpcJsonContext.Default.DeleteCustomUserColorsEndpointResponse,
            ct,
            () => EnsureRawInput(ValidRawCollectionCount(customUserColorIds, requireNonEmpty: true)));
    }

    public async Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.ExportCustomUserColorsToUser,
            () => new ExportCustomUserColorsToUserEndpointRequest { SourceToken = sourceToken, Request = request },
            EndpointRpcJsonContext.Default.ExportCustomUserColorsToUserEndpointRequest,
            EndpointRpcJsonContext.Default.ExportCustomUserColorsToUserEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawCollectionCount(request.CustomUserColorIds, requireNonEmpty: true)));
    }

    public async Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.UpdateCustomUserColor,
            () => new UpdateCustomUserColorEndpointRequest { Token = token, Request = request },
            EndpointRpcJsonContext.Default.UpdateCustomUserColorEndpointRequest,
            EndpointRpcJsonContext.Default.UpdateCustomUserColorEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidOptionalRawString(request.ColorName, 1, DataLengthConstants.CustomUserColorNameMaxLength) &&
                ValidOptionalExactRawString(request.ColorCode, DataLengthConstants.ARGBColorLength)));
    }

    public async Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.AddPasswordTag,
            () => new AddPasswordTagEndpointRequest { Token = token, Request = request },
            EndpointRpcJsonContext.Default.AddPasswordTagEndpointRequest,
            EndpointRpcJsonContext.Default.AddPasswordTagEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawString(request.Name, 1, DataLengthConstants.PasswordTagNameMaxLength) &&
                ValidExactRawString(request.Color, DataLengthConstants.ARGBColorLength)));
    }

    public async Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.DeletePasswordTag,
            () => new DeletePasswordTagEndpointRequest { Token = token, PasswordTagId = passwordTagId },
            EndpointRpcJsonContext.Default.DeletePasswordTagEndpointRequest,
            EndpointRpcJsonContext.Default.DeletePasswordTagEndpointResponse,
            ct);
    }

    public async Task ExportPasswordTagsToUserAsync(Guid sourceToken, ExportPasswordTagsToUserRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.ExportPasswordTagsToUser,
            () => new ExportPasswordTagsToUserEndpointRequest { SourceToken = sourceToken, Request = request },
            EndpointRpcJsonContext.Default.ExportPasswordTagsToUserEndpointRequest,
            EndpointRpcJsonContext.Default.ExportPasswordTagsToUserEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidRawCollectionCount(request.PasswordTagIds, requireNonEmpty: true)));
    }

    public async Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default)
    {
        await InvokeAsync(
            EndpointOperationId.UpdatePasswordTag,
            () => new UpdatePasswordTagEndpointRequest { Token = token, Request = request },
            EndpointRpcJsonContext.Default.UpdatePasswordTagEndpointRequest,
            EndpointRpcJsonContext.Default.UpdatePasswordTagEndpointResponse,
            ct,
            () => EnsureRawInput(request is not null &&
                ValidOptionalRawString(request.Name, 1, DataLengthConstants.PasswordTagNameMaxLength) &&
                ValidOptionalExactRawString(request.Color, DataLengthConstants.ARGBColorLength)));
    }

    private async Task<TResponse> InvokeAsync<TRequest, TResponse>(
        EndpointOperationId operationId,
        Func<TRequest> requestFactory,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        Action? validateRawInput = null)
        where TRequest : class
        where TResponse : class
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requestFactory);
        var descriptor = EndpointOperationManifest.Get(operationId);
        validateRawInput?.Invoke();
        TRequest? request = null;
        byte[]? requestPayload = null;
        byte[]? responsePayload = null;
        TResponse? response = null;
        var responseTransferred = false;
        var conclusiveSuccessResponseReceived = false;
        EndpointRpcAdmissionLease? admission = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            admission = await AcquireAdmissionAsync(cancellationToken);
            request = requestFactory();
            _validator.ValidateRequest(operationId, request);
            requestPayload = _serializer.Serialize(request, requestTypeInfo);
            cancellationToken.ThrowIfCancellationRequested();
            if (requestPayload.Length > descriptor.MaximumRequestPayloadSize)
                throw new EndpointRpcPayloadException("The endpoint RPC request payload exceeds the permitted size.");

            var transportResponse = await _transport.SendAsync(
                operationId,
                requestPayload,
                descriptor.CancellationClassification,
                cancellationToken);
            conclusiveSuccessResponseReceived = true;
            responsePayload = transportResponse.ResponseKind switch
            {
                EndpointRpcResponseKind.InlineResult when transportResponse.InlinePayload is not null =>
                    transportResponse.InlinePayload,
                EndpointRpcResponseKind.LargeResult when transportResponse.LargeResult is not null && descriptor.SupportsLargeResponse =>
                    await ReceiveLargeResultAsync(transportResponse.LargeResult, cancellationToken),
                _ => throw new EndpointRpcPayloadException("The endpoint RPC response form is invalid.")
            };

            if (responsePayload.Length > descriptor.MaximumLogicalResponsePayloadSize)
                throw new EndpointRpcPayloadException("The endpoint RPC response payload exceeds the permitted size.");

            response = _serializer.Deserialize(responsePayload, responseTypeInfo);
            _validator.ValidateResponse(operationId, response);
            responseTransferred = true;
            return response;
        }
        catch (EndpointRpcTransportException exception)
        {
            if (descriptor.MutatesState &&
                exception.TransmissionState != EndpointRpcTransmissionState.DefinitelyNotSent)
            {
                throw CreateOutcomeUnknownException(operationId, exception);
            }

            Rethrow(exception.InnerException ?? exception);
            throw;
        }
        catch (EndpointRpcRemoteException exception)
        {
            try
            {
                _validator.Validate(operationId, exception.Error);
            }
            catch (Exception validationException) when (descriptor.MutatesState)
            {
                throw new EndpointOperationOutcomeUnknownException(
                    operationId,
                    requiresProcessRestart: false,
                    validationException);
            }

            throw MapRemoteFailure(operationId, exception);
        }
        catch (Exception exception)
            when (descriptor.MutatesState && conclusiveSuccessResponseReceived)
        {
            throw CreateOutcomeUnknownException(operationId, exception);
        }
        finally
        {
            admission?.Dispose();
            if (!responseTransferred && response is not null)
                EndpointSensitiveData.ClearResponse(operationId, response);
            if (request is not null)
                EndpointSensitiveData.ClearRequest(operationId, request);
            EndpointSensitiveData.Clear(requestPayload);
            EndpointSensitiveData.Clear(responsePayload);
        }
    }

    private async Task<byte[]> ReceiveLargeResultAsync(
        EndpointLargeResultDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            _largeResultValidator.Validate(descriptor);
            if (descriptor.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new EndpointRpcPayloadException("The endpoint large-result transfer has expired.");
        }
        catch
        {
            await ReleaseLargeResultBestEffortAsync(descriptor);
            throw;
        }

        var assembled = new byte[descriptor.DeclaredTotalLength];
        var completed = false;
        try
        {
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < descriptor.DeclaredChunkCount; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new GetEndpointLargeResultChunkRequest
                {
                    TransferId = descriptor.TransferId,
                    OriginalCorrelationId = descriptor.OriginalCorrelationId,
                    ChunkIndex = chunkIndex
                };
                _largeResultValidator.Validate(request);
                var requestPayload = _serializer.Serialize(
                    request,
                    EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkRequest);
                byte[]? responsePayload = null;
                GetEndpointLargeResultChunkResponse? response = null;
                try
                {
                    responsePayload = await _transport.GetLargeResultChunkAsync(
                        requestPayload,
                        cancellationToken);
                    response = _serializer.Deserialize(
                        responsePayload,
                        EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse);
                    _largeResultValidator.Validate(response);
                    ValidateChunk(descriptor, response, chunkIndex, offset);
                    response.ChunkPayload.CopyTo(assembled.AsSpan(offset));
                    offset = checked(offset + response.ChunkPayload.Length);
                }
                finally
                {
                    if (response is not null)
                        EndpointSensitiveData.Clear(response.ChunkPayload);
                    EndpointSensitiveData.Clear(requestPayload);
                    EndpointSensitiveData.Clear(responsePayload);
                }
            }

            if (offset != descriptor.DeclaredTotalLength)
                throw new EndpointRpcPayloadException("The endpoint large-result assembled length is invalid.");
            completed = true;
            return assembled;
        }
        finally
        {
            await ReleaseLargeResultBestEffortAsync(descriptor);
            if (!completed)
                EndpointSensitiveData.Clear(assembled);
        }
    }

    private async Task ReleaseLargeResultBestEffortAsync(
        EndpointLargeResultDescriptor descriptor)
    {
        byte[]? payload = null;
        try
        {
            if (!_transport.IsConnected)
                return;
            var request = new ReleaseEndpointLargeResultRequest
            {
                TransferId = descriptor.TransferId,
                OriginalCorrelationId = descriptor.OriginalCorrelationId
            };
            _largeResultValidator.Validate(request);
            payload = _serializer.Serialize(
                request,
                EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultRequest);
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(EndpointRpcLimits.LargeResultReleaseTimeoutSeconds));
            await _transport.ReleaseLargeResultAsync(payload, timeoutSource.Token);
        }
        catch
        {
        }
        finally
        {
            EndpointSensitiveData.Clear(payload);
        }
    }

    private static void ValidateChunk(
        EndpointLargeResultDescriptor descriptor,
        GetEndpointLargeResultChunkResponse response,
        int expectedChunkIndex,
        int currentOffset)
    {
        if (response.TransferId != descriptor.TransferId ||
            response.OriginalCorrelationId != descriptor.OriginalCorrelationId ||
            response.ChunkIndex != expectedChunkIndex ||
            response.DeclaredTotalLength != descriptor.DeclaredTotalLength ||
            response.DeclaredChunkCount != descriptor.DeclaredChunkCount ||
            response.IsFinal != (expectedChunkIndex == descriptor.DeclaredChunkCount - 1))
        {
            throw new EndpointRpcPayloadException("The endpoint large-result chunk does not match its transfer.");
        }

        var nextOffset = checked(currentOffset + response.ChunkPayload.Length);
        if (nextOffset > descriptor.DeclaredTotalLength ||
            (!response.IsFinal && response.ChunkPayload.Length != descriptor.ChunkSize) ||
            (response.IsFinal && nextOffset != descriptor.DeclaredTotalLength))
        {
            throw new EndpointRpcPayloadException("The endpoint large-result chunk length is invalid.");
        }
    }

    private async Task<EndpointRpcAdmissionLease> AcquireAdmissionAsync(
        CancellationToken callerCancellationToken)
    {
        ThrowIfDisposed();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            _disposeSource.Token);
        try
        {
            await _operationCapacity.WaitAsync(linkedSource.Token);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _operationCapacity.Release();
                throw new ObjectDisposedException(nameof(NamedPipeEndpointsProxy));
            }

            return new EndpointRpcAdmissionLease(_operationCapacity);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerCancellationToken);
        }
        catch (OperationCanceledException exception) when (_disposeSource.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(NamedPipeEndpointsProxy), exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeSource.Cancel();
        for (var index = 0; index < _options.MaximumConcurrentOperations; index++)
            await _operationCapacity.WaitAsync(CancellationToken.None);
        _operationCapacity.Dispose();
        _disposeSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void EnsureRawInput(bool isValid)
    {
        if (!isValid)
            throw new EndpointRpcPayloadException("The endpoint RPC input is semantically invalid.");
    }

    private static bool ValidRawString(string? value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength;

    private static bool ValidOptionalRawString(
        string? value,
        int minimumLength,
        int maximumLength) =>
        value is null || ValidRawString(value, minimumLength, maximumLength);

    private static bool ValidExactRawString(string? value, int exactLength) =>
        value is not null && value.Length == exactLength;

    private static bool ValidOptionalExactRawString(string? value, int exactLength) =>
        value is null || value.Length == exactLength;

    private static bool ValidRawBytes(byte[]? value) =>
        value is not null &&
        value.Length >= DataLengthConstants.PasswordMinLength &&
        value.Length <= DataLengthConstants.PasswordMaxLength;

    private static bool ValidOptionalRawBytes(byte[]? value) =>
        value is null || ValidRawBytes(value);

    private static bool ValidOptionalRawCollectionCount<T>(IReadOnlyCollection<T>? values) =>
        values is null || values.Count <= EndpointRpcLimits.MaximumCollectionItems;

    private static bool ValidRawCollectionCount<T>(
        IReadOnlyCollection<T>? values,
        bool requireNonEmpty)
    {
        if (values is null || values.Count > EndpointRpcLimits.MaximumCollectionItems)
            return false;
        return !requireNonEmpty || values.Count > 0;
    }

    private static Exception MapRemoteFailure(
        EndpointOperationId operationId,
        EndpointRpcRemoteException exception)
    {
        return exception.Error.ErrorCode switch
        {
            EndpointRpcErrorCode.OperationPartiallyCommitted =>
                new EndpointOperationPartiallyCommittedException(
                    operationId,
                    exception.Error.Recovery!,
                    exception.Error.RequiresProcessRestart,
                    exception),
            EndpointRpcErrorCode.OperationOutcomeUnknown =>
                new EndpointOperationOutcomeUnknownException(
                    operationId,
                    exception.Error.RequiresProcessRestart,
                    exception),
            EndpointRpcErrorCode.OperationCancelled =>
                new OperationCanceledException(exception.Error.SafeMessage, exception),
            EndpointRpcErrorCode.Disconnected =>
                new EndpointRpcDisconnectedException(exception),
            _ => exception
        };
    }

    private static EndpointOperationOutcomeUnknownException CreateOutcomeUnknownException(
        EndpointOperationId operationId,
        Exception exception)
    {
        var remoteError = exception switch
        {
            EndpointRpcRemoteException remoteException => remoteException.Error,
            EndpointRpcTransportException { InnerException: EndpointRpcRemoteException remoteException } =>
                remoteException.Error,
            _ => null
        };
        return new EndpointOperationOutcomeUnknownException(
            operationId,
            remoteError?.RequiresProcessRestart ?? false,
            exception);
    }

    private static void Rethrow(Exception exception)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(NamedPipeEndpointsProxy));
    }
}
