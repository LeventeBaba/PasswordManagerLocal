using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public class ThrowingRecordingEndpoints : IEndpoints
{
    public string? LastMethodName { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    private Task Throw(string methodName, CancellationToken cancellationToken)
    {
        LastMethodName = methodName;
        LastCancellationToken = cancellationToken;
        return Task.FromException(new TestEndpointInvocationException());
    }

    private Task<T> Throw<T>(string methodName, CancellationToken cancellationToken)
    {
        LastMethodName = methodName;
        LastCancellationToken = cancellationToken;
        return Task.FromException<T>(new TestEndpointInvocationException());
    }

    public virtual Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default) => Throw<Guid>(nameof(RegisterAsync), ct);
    public virtual Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default) => Throw<Guid>(nameof(LoginAsync), ct);
    public virtual Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default) => Throw<Guid>(nameof(RenewAuthSessionAsync), ct);
    public virtual Task LogoutAsync(Guid token, CancellationToken ct = default) => Throw(nameof(LogoutAsync), ct);
    public virtual Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default) => Throw<AuthSessionStatusResponse>(nameof(GetAuthSessionStatusAsync), ct);
    public virtual Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default) => Throw(nameof(ChangeMasterPasswordAsync), ct);
    public virtual Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default) => Throw<UserProfileInfoResponse>(nameof(GetUserProfileInfoAsync), ct);
    public virtual Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default) => Throw(nameof(DeleteUserAccountAsync), ct);
    public virtual Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default) => Throw(nameof(ChangeUsernameAsync), ct);
    public virtual Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default) => Throw(nameof(UpdateUserProfileInfoAsync), ct);
    public virtual Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) => Throw<LocalDeviceInfoResponse>(nameof(GetLocalDeviceInfoAsync), ct);
    public virtual Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default) => Throw<bool>(nameof(GetLocalUserSyncOnAsync), ct);
    public virtual Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default) => Throw(nameof(SetLocalUserSyncOnAsync), ct);
    public virtual Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default) => Throw(nameof(SetLocalDeviceNameAsync), ct);
    public virtual Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default) => Throw<IReadOnlyList<UserDeviceInfoResponse>>(nameof(GetUserDevicesAsync), ct);
    public virtual Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default) => Throw(nameof(SetUserDeviceNameAsync), ct);
    public virtual Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default) => Throw(nameof(SetUserDeviceSyncOnAsync), ct);
    public virtual Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default) => Throw(nameof(UnblockUserDeviceAsync), ct);
    public virtual Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default) => Throw<DeviceRemovalResultResponse>(nameof(DisconnectUserDeviceAsync), ct);
    public virtual Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default) => Throw<DeviceEnrollmentCodeResponse>(nameof(StartDeviceEnrollmentAsync), ct);
    public virtual Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default) => Throw<DeviceEnrollmentStatusResponse>(nameof(GetDeviceEnrollmentStatusAsync), ct);
    public virtual Task CancelDeviceEnrollmentAsync(CancellationToken ct = default) => Throw(nameof(CancelDeviceEnrollmentAsync), ct);
    public virtual Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default) => Throw(nameof(AddDeviceByCodeAsync), ct);
    public virtual Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default) => Throw<IReadOnlyList<Guid>>(nameof(RestoreRememberedSessionsAsync), ct);
    public virtual Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default) => Throw<Guid>(nameof(InitializeRememberMeSessionAsync), ct);
    public virtual Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default) => Throw(nameof(SetRememberMeAsync), ct);
    public virtual Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default) => Throw<SavedPasswordsResponse>(nameof(GetSavedPasswordsAsync), ct);
    public virtual Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default) => Throw(nameof(AddNewPasswordAsync), ct);
    public virtual Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default) => Throw(nameof(RemovePasswordsAsync), ct);
    public virtual Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default) => Throw<byte[]>(nameof(GetUnsecurePasswordAsync), ct);
    public virtual Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default) => Throw(nameof(UpdatePasswordAsync), ct);
    public virtual Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default) => Throw(nameof(ExportPasswordsToUserAsync), ct);
    public virtual Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default) => Throw(nameof(AddCustomUserColorsAsync), ct);
    public virtual Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default) => Throw(nameof(DeleteCustomUserColorsAsync), ct);
    public virtual Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default) => Throw(nameof(ExportCustomUserColorsToUserAsync), ct);
    public virtual Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default) => Throw(nameof(UpdateCustomUserColorAsync), ct);
    public virtual Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default) => Throw(nameof(AddPasswordTagAsync), ct);
    public virtual Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default) => Throw(nameof(DeletePasswordTagAsync), ct);
    public virtual Task ExportPasswordTagsToUserAsync(Guid sourceToken, ExportPasswordTagsToUserRequest request, CancellationToken ct = default) => Throw(nameof(ExportPasswordTagsToUserAsync), ct);
    public virtual Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default) => Throw(nameof(UpdatePasswordTagAsync), ct);
}
