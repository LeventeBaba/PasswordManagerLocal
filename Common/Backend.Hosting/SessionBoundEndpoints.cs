using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal sealed class SessionBoundEndpoints : IEndpoints
{
    private readonly InteractiveBackendSession _session;

    public SessionBoundEndpoints(InteractiveBackendSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.RegisterAsync(request, ct));

    public Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.LoginAsync(request, ct));

    public Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.RenewAuthSessionAsync(token, ct));

    public Task LogoutAsync(Guid token, CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.LogoutAsync(token, ct));

    public Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(
        Guid token,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetAuthSessionStatusAsync(token, ct));

    public Task ChangeMasterPasswordAsync(
        MasterPasswordChangeRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.ChangeMasterPasswordAsync(request, ct));

    public Task<UserProfileInfoResponse> GetUserProfileInfoAsync(
        Guid token,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetUserProfileInfoAsync(token, ct));

    public Task DeleteUserAccountAsync(
        Guid token,
        byte[] password,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.DeleteUserAccountAsync(token, password, ct));

    public Task ChangeUsernameAsync(
        Guid token,
        string newUsername,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.ChangeUsernameAsync(token, newUsername, ct));

    public Task UpdateUserProfileInfoAsync(
        UpdateUserProfileRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.UpdateUserProfileInfoAsync(request, ct));

    public Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetLocalDeviceInfoAsync(ct));

    public Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetLocalUserSyncOnAsync(token, ct));

    public Task SetLocalUserSyncOnAsync(
        Guid token,
        bool isSyncOn,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.SetLocalUserSyncOnAsync(token, isSyncOn, ct));

    public Task SetLocalDeviceNameAsync(
        Guid token,
        string name,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.SetLocalDeviceNameAsync(token, name, ct));

    public Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(
        Guid token,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetUserDevicesAsync(token, ct));

    public Task SetUserDeviceNameAsync(
        Guid token,
        Guid deviceId,
        string name,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.SetUserDeviceNameAsync(token, deviceId, name, ct));

    public Task SetUserDeviceSyncOnAsync(
        Guid token,
        Guid deviceId,
        bool isSyncOn,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.SetUserDeviceSyncOnAsync(token, deviceId, isSyncOn, ct));

    public Task UnblockUserDeviceAsync(
        Guid token,
        Guid deviceId,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.UnblockUserDeviceAsync(token, deviceId, ct));

    public Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(
        Guid token,
        Guid deviceId,
        byte[] masterPassword,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.DisconnectUserDeviceAsync(
            token,
            deviceId,
            masterPassword,
            ct));

    public Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.StartDeviceEnrollmentAsync(ct));

    public Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetDeviceEnrollmentStatusAsync(ct));

    public Task CancelDeviceEnrollmentAsync(CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.CancelDeviceEnrollmentAsync(ct));

    public Task AddDeviceByCodeAsync(
        Guid token,
        string code,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.AddDeviceByCodeAsync(token, code, ct));

    public Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.RestoreRememberedSessionsAsync(ct));

    public Task<Guid> InitializeRememberMeSessionAsync(
        Guid userId,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.InitializeRememberMeSessionAsync(userId, ct));

    public Task SetRememberMeAsync(
        Guid token,
        bool rememberMe,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.SetRememberMeAsync(token, rememberMe, ct));

    public Task<SavedPasswordsResponse> GetSavedPasswordsAsync(
        Guid token,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetSavedPasswordsAsync(token, ct));

    public Task AddNewPasswordAsync(
        Guid token,
        NewPasswordRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.AddNewPasswordAsync(token, request, ct));

    public Task RemovePasswordsAsync(
        Guid token,
        IReadOnlyList<Guid> passwordIds,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.RemovePasswordsAsync(token, passwordIds, ct));

    public Task<byte[]> GetUnsecurePasswordAsync(
        Guid token,
        Guid passwordId,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.GetUnsecurePasswordAsync(token, passwordId, ct));

    public Task UpdatePasswordAsync(
        Guid token,
        UpdatePasswordRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.UpdatePasswordAsync(token, request, ct));

    public Task ExportPasswordsToUserAsync(
        Guid sourceToken,
        ExportPasswordsToUserRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.ExportPasswordsToUserAsync(sourceToken, request, ct));

    public Task AddCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<NewCustomUserColorRequest> requests,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.AddCustomUserColorsAsync(token, requests, ct));

    public Task DeleteCustomUserColorsAsync(
        Guid token,
        IReadOnlyList<Guid> customUserColorIds,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.DeleteCustomUserColorsAsync(token, customUserColorIds, ct));

    public Task ExportCustomUserColorsToUserAsync(
        Guid sourceToken,
        ExportCustomUserColorsToUserRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.ExportCustomUserColorsToUserAsync(
            sourceToken,
            request,
            ct));

    public Task UpdateCustomUserColorAsync(
        Guid token,
        UpdateCustomUserColorRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.UpdateCustomUserColorAsync(token, request, ct));

    public Task AddPasswordTagAsync(
        Guid token,
        NewPasswordTagRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.AddPasswordTagAsync(token, request, ct));

    public Task DeletePasswordTagAsync(
        Guid token,
        Guid passwordTagId,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.DeletePasswordTagAsync(token, passwordTagId, ct));

    public Task ExportPasswordTagsToUserAsync(
        Guid sourceToken,
        ExportPasswordTagsToUserRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.ExportPasswordTagsToUserAsync(sourceToken, request, ct));

    public Task UpdatePasswordTagAsync(
        Guid token,
        UpdatePasswordTagRequest request,
        CancellationToken ct = default) =>
        _session.ExecuteAsync(endpoints => endpoints.UpdatePasswordTagAsync(token, request, ct));
}
