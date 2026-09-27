using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Contracts.Endpoints;

public interface IEndpoints
{
    Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default);
    Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default);
    Task LogoutAsync(Guid token, CancellationToken ct = default);
    Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default);
    Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default);


    Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default);
    Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default);
    Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default);
    Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default);


    Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default);
    Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default);
    Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default);
    Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default);
    Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default);
    Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default);
    Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default);
    Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default);
    Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default);
    Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default);
    Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default);
    Task CancelDeviceEnrollmentAsync(CancellationToken ct = default);
    Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default);


    Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default);
    Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default);
    Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default);


    Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default);
    Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default);
    Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default);
    Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default);
    Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default);
    Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default);
    Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default);
    Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default);
    Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default);
    Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default);
    Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default);
    Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default);
    Task ExportPasswordTagsToUserAsync(Guid sourceToken, ExportPasswordTagsToUserRequest request, CancellationToken ct = default);
    Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default);
}
