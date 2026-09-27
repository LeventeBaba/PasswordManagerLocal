using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Android.Runtime;

public sealed class AndroidAttachmentAuthorizedEndpoints : IEndpoints
{
    private readonly AndroidRuntimeServiceHost _owner;
    private readonly AndroidServiceFrontendBackendClient _client;
    private readonly IEndpoints _inner;

    internal AndroidAttachmentAuthorizedEndpoints(
        AndroidRuntimeServiceHost owner,
        AndroidServiceFrontendBackendClient client,
        IEndpoints inner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.RegisterAsync(request, ct));

    public Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.LoginAsync(request, ct));

    public Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.RenewAuthSessionAsync(token, ct));

    public Task LogoutAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.LogoutAsync(token, ct));

    public Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetAuthSessionStatusAsync(token, ct));

    public Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.ChangeMasterPasswordAsync(request, ct));

    public Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetUserProfileInfoAsync(token, ct));

    public Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.DeleteUserAccountAsync(token, password, ct));

    public Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.ChangeUsernameAsync(token, newUsername, ct));

    public Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.UpdateUserProfileInfoAsync(request, ct));

    public Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetLocalDeviceInfoAsync(ct));

    public Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetLocalUserSyncOnAsync(token, ct));

    public Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.SetLocalUserSyncOnAsync(token, isSyncOn, ct));

    public Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.SetLocalDeviceNameAsync(token, name, ct));

    public Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetUserDevicesAsync(token, ct));

    public Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.SetUserDeviceNameAsync(token, deviceId, name, ct));

    public Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.SetUserDeviceSyncOnAsync(token, deviceId, isSyncOn, ct));

    public Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.UnblockUserDeviceAsync(token, deviceId, ct));

    public Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.DisconnectUserDeviceAsync(token, deviceId, masterPassword, ct));

    public Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default) =>
        InvokeAsync(() => _inner.StartDeviceEnrollmentAsync(ct));

    public Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetDeviceEnrollmentStatusAsync(ct));

    public Task CancelDeviceEnrollmentAsync(CancellationToken ct = default) =>
        InvokeAsync(() => _inner.CancelDeviceEnrollmentAsync(ct));

    public Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.AddDeviceByCodeAsync(token, code, ct));

    public Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default) =>
        InvokeAsync(() => _inner.RestoreRememberedSessionsAsync(ct));

    public Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.InitializeRememberMeSessionAsync(userId, ct));

    public Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.SetRememberMeAsync(token, rememberMe, ct));

    public Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetSavedPasswordsAsync(token, ct));

    public Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.AddNewPasswordAsync(token, request, ct));

    public Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.RemovePasswordsAsync(token, passwordIds, ct));

    public Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.GetUnsecurePasswordAsync(token, passwordId, ct));

    public Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.UpdatePasswordAsync(token, request, ct));

    public Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.ExportPasswordsToUserAsync(sourceToken, request, ct));

    public Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.AddCustomUserColorsAsync(token, requests, ct));

    public Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.DeleteCustomUserColorsAsync(token, customUserColorIds, ct));

    public Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.ExportCustomUserColorsToUserAsync(sourceToken, request, ct));

    public Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.UpdateCustomUserColorAsync(token, request, ct));

    public Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.AddPasswordTagAsync(token, request, ct));

    public Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.DeletePasswordTagAsync(token, passwordTagId, ct));

    public Task ExportPasswordTagsToUserAsync(Guid sourceToken, ExportPasswordTagsToUserRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.ExportPasswordTagsToUserAsync(sourceToken, request, ct));

    public Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default) =>
        InvokeAsync(() => _inner.UpdatePasswordTagAsync(token, request, ct));

    private Task InvokeAsync(Func<Task> operation)
    {
        _owner.EnsureEndpointAuthority(_client);
        return operation();
    }

    private Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        _owner.EnsureEndpointAuthority(_client);
        return operation();
    }
}
