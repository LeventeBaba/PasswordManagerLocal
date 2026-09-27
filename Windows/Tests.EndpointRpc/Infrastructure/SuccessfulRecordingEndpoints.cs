using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class SuccessfulRecordingEndpoints : IEndpoints
{
    public List<string> Calls { get; } = [];

    private void Record(string methodName) => Calls.Add(methodName);

    public Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default) { Record(nameof(RegisterAsync)); return Task.FromResult(EndpointRpcTestData.Token); }
    public Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default) { Record(nameof(LoginAsync)); return Task.FromResult(EndpointRpcTestData.Token); }
    public Task<Guid> RenewAuthSessionAsync(Guid token, CancellationToken ct = default) { Record(nameof(RenewAuthSessionAsync)); return Task.FromResult(EndpointRpcTestData.OtherToken); }
    public Task LogoutAsync(Guid token, CancellationToken ct = default) { Record(nameof(LogoutAsync)); return Task.CompletedTask; }
    public Task<AuthSessionStatusResponse> GetAuthSessionStatusAsync(Guid token, CancellationToken ct = default) { Record(nameof(GetAuthSessionStatusAsync)); return Task.FromResult(new AuthSessionStatusResponse { IsAuthenticated = true, InvalidationReason = AuthSessionInvalidationReason.None, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) }); }
    public Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default) { Record(nameof(ChangeMasterPasswordAsync)); return Task.CompletedTask; }
    public Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default) { Record(nameof(GetUserProfileInfoAsync)); return Task.FromResult(new UserProfileInfoResponse { UId = EndpointRpcTestData.ItemId, Username = "ValidUser1", FirstName = "Levente", LastName = "Baba", Email = "valid@example.test", RegistrationDate = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc), RegistrationTimeZoneId = "Europe/Budapest", RegistrationDeviceType = DeviceType.WindowsPc }); }
    public Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default) { Record(nameof(DeleteUserAccountAsync)); return Task.CompletedTask; }
    public Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default) { Record(nameof(ChangeUsernameAsync)); return Task.CompletedTask; }
    public Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default) { Record(nameof(UpdateUserProfileInfoAsync)); return Task.CompletedTask; }
    public Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) { Record(nameof(GetLocalDeviceInfoAsync)); return Task.FromResult(new LocalDeviceInfoResponse { DeviceId = EndpointRpcTestData.ItemId, TlsCertFingerprint = "fingerprint", DeviceType = DeviceType.WindowsPc, CreatedAt = DateTimeOffset.UtcNow }); }
    public Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default) { Record(nameof(GetLocalUserSyncOnAsync)); return Task.FromResult(true); }
    public Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default) { Record(nameof(SetLocalUserSyncOnAsync)); return Task.CompletedTask; }
    public Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default) { Record(nameof(SetLocalDeviceNameAsync)); return Task.CompletedTask; }
    public Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default) { Record(nameof(GetUserDevicesAsync)); return Task.FromResult<IReadOnlyList<UserDeviceInfoResponse>>([]); }
    public Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default) { Record(nameof(SetUserDeviceNameAsync)); return Task.CompletedTask; }
    public Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default) { Record(nameof(SetUserDeviceSyncOnAsync)); return Task.CompletedTask; }
    public Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default) { Record(nameof(UnblockUserDeviceAsync)); return Task.CompletedTask; }
    public Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default) { Record(nameof(DisconnectUserDeviceAsync)); return Task.FromResult(new DeviceRemovalResultResponse { Removed = true, Message = "Removed", ResultingMembershipEpoch = 1 }); }
    public Task<DeviceEnrollmentCodeResponse> StartDeviceEnrollmentAsync(CancellationToken ct = default) { Record(nameof(StartDeviceEnrollmentAsync)); return Task.FromResult(new DeviceEnrollmentCodeResponse { Code = "ABCD2345", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }); }
    public Task<DeviceEnrollmentStatusResponse> GetDeviceEnrollmentStatusAsync(CancellationToken ct = default) { Record(nameof(GetDeviceEnrollmentStatusAsync)); return Task.FromResult(new DeviceEnrollmentStatusResponse()); }
    public Task CancelDeviceEnrollmentAsync(CancellationToken ct = default) { Record(nameof(CancelDeviceEnrollmentAsync)); return Task.CompletedTask; }
    public Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default) { Record(nameof(AddDeviceByCodeAsync)); return Task.CompletedTask; }
    public Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default) { Record(nameof(RestoreRememberedSessionsAsync)); return Task.FromResult<IReadOnlyList<Guid>>([EndpointRpcTestData.Token]); }
    public Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default) { Record(nameof(InitializeRememberMeSessionAsync)); return Task.FromResult(EndpointRpcTestData.Token); }
    public Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default) { Record(nameof(SetRememberMeAsync)); return Task.CompletedTask; }
    public Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default) { Record(nameof(GetSavedPasswordsAsync)); return Task.FromResult(new SavedPasswordsResponse()); }
    public Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default) { Record(nameof(AddNewPasswordAsync)); return Task.CompletedTask; }
    public Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default) { Record(nameof(RemovePasswordsAsync)); return Task.CompletedTask; }
    public Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default) { Record(nameof(GetUnsecurePasswordAsync)); return Task.FromResult(EndpointRpcTestData.Password()); }
    public Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default) { Record(nameof(UpdatePasswordAsync)); return Task.CompletedTask; }
    public Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default) { Record(nameof(ExportPasswordsToUserAsync)); return Task.CompletedTask; }
    public Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default) { Record(nameof(AddCustomUserColorsAsync)); return Task.CompletedTask; }
    public Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default) { Record(nameof(DeleteCustomUserColorsAsync)); return Task.CompletedTask; }
    public Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default) { Record(nameof(ExportCustomUserColorsToUserAsync)); return Task.CompletedTask; }
    public Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default) { Record(nameof(UpdateCustomUserColorAsync)); return Task.CompletedTask; }
    public Task AddPasswordTagAsync(Guid token, NewPasswordTagRequest request, CancellationToken ct = default) { Record(nameof(AddPasswordTagAsync)); return Task.CompletedTask; }
    public Task DeletePasswordTagAsync(Guid token, Guid passwordTagId, CancellationToken ct = default) { Record(nameof(DeletePasswordTagAsync)); return Task.CompletedTask; }
    public Task ExportPasswordTagsToUserAsync(Guid sourceToken, ExportPasswordTagsToUserRequest request, CancellationToken ct = default) { Record(nameof(ExportPasswordTagsToUserAsync)); return Task.CompletedTask; }
    public Task UpdatePasswordTagAsync(Guid token, UpdatePasswordTagRequest request, CancellationToken ct = default) { Record(nameof(UpdatePasswordTagAsync)); return Task.CompletedTask; }
}
