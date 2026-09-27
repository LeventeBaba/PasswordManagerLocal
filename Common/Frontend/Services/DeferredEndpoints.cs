using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class DeferredEndpoints : IEndpoints
{
    private readonly IFrontendBackendClient<IEndpoints> _backendClient;
    private readonly CancellationToken _lifetime;

    public DeferredEndpoints(IFrontendBackendClient<IEndpoints> backendClient, CancellationToken lifetime = default)
    {
        _lifetime = lifetime;
        _backendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
    }
    public async Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.RegisterAsync(FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.LoginAsync(FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.RenewAuthSessionAsync(token, operationToken), ct);
    }


    public async Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.LogoutAsync(token, operationToken), ct);
    }


    public async Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetAuthSessionStatusAsync(token, operationToken), ct);
    }


    public async Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.ChangeMasterPasswordAsync(FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetUserProfileInfoAsync(token, operationToken), ct);
    }


    public async Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.DeleteUserAccountAsync(token, password, operationToken), ct);
    }


    public async Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.ChangeUsernameAsync(token, newUsername, operationToken), ct);
    }


    public async Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.UpdateUserProfileInfoAsync(FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetLocalDeviceInfoAsync(operationToken), ct);
    }


    public async Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetLocalUserSyncOnAsync(token, operationToken), ct);
    }


    public async Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.SetLocalUserSyncOnAsync(token, isSyncOn, operationToken), ct);
    }


    public async Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.SetLocalDeviceNameAsync(token, name, operationToken), ct);
    }


    public async Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetUserDevicesAsync(token, operationToken), ct);
    }


    public async Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.SetUserDeviceNameAsync(token, deviceId, name, operationToken), ct);
    }


    public async Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.SetUserDeviceSyncOnAsync(token, deviceId, isSyncOn, operationToken), ct);
    }


    public async Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.UnblockUserDeviceAsync(token, deviceId, operationToken), ct);
    }


    public async Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.DisconnectUserDeviceAsync(token, deviceId, masterPassword, operationToken), ct);
    }


    public async Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.StartDeviceEnrollmentAsync(operationToken), ct);
    }


    public async Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetDeviceEnrollmentStatusAsync(operationToken), ct);
    }


    public async Task CancelDeviceEnrollmentAsync(CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.CancelDeviceEnrollmentAsync(operationToken), ct);
    }


    public async Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.AddDeviceByCodeAsync(token, code, operationToken), ct);
    }


    public async Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.RestoreRememberedSessionsAsync(operationToken), ct);
    }


    public async Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.InitializeRememberMeSessionAsync(userId, operationToken), ct);
    }


    public async Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.SetRememberMeAsync(token, rememberMe, operationToken), ct);
    }


    public async Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetSavedPasswordsAsync(token, operationToken), ct);
    }


    public async Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.AddNewPasswordAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task RemovePasswordsAsync(
        Guid token,
        IReadOnlyList<Guid> passwordIds,
        CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.RemovePasswordsAsync(token, passwordIds, operationToken), ct);
    }


    public async Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default)
    {
        return await InvokeAsync((endpoints, operationToken) => endpoints.GetUnsecurePasswordAsync(token, passwordId, operationToken), ct);
    }


    public async Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.UpdatePasswordAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }

    public async Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.ExportPasswordsToUserAsync(sourceToken, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task AddCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<NewCustomUserColorRequest> requests,
        CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.AddCustomUserColorsAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(requests), operationToken), ct);
    }


    public async Task DeleteCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<Guid> customUserColorIds,
        CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.DeleteCustomUserColorsAsync(token, customUserColorIds, operationToken), ct);
    }


    public async Task ExportCustomUserColorsToUserAsync(
        Guid sourceToken,
        ExportCustomUserColorsToUserRequest request,
        CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.ExportCustomUserColorsToUserAsync(sourceToken, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.UpdateCustomUserColorAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.AddPasswordTagAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.DeletePasswordTagAsync(token, passwordTagId, operationToken), ct);
    }


    public async Task ExportPasswordTagsToUserAsync(
        Guid sourceToken,
        ExportPasswordTagsToUserRequest request,
        CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.ExportPasswordTagsToUserAsync(sourceToken, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    public async Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default)
    {
        await InvokeAsync((endpoints, operationToken) => endpoints.UpdatePasswordTagAsync(token, FrontendDateTimeUtil.NormalizeRequestToUtc(request), operationToken), ct);
    }


    private async Task<T> InvokeAsync<T>(
        Func<IEndpoints, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var endpoints = await _backendClient.GetEndpointsAsync(token);
        token.ThrowIfCancellationRequested();
        var result = await action(endpoints, token);
        if (token.IsCancellationRequested)
        {
            if (result is byte[] secret)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    private async Task InvokeAsync(
        Func<IEndpoints, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var endpoints = await _backendClient.GetEndpointsAsync(token);
        token.ThrowIfCancellationRequested();
        await action(endpoints, token);
    }
}
