using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Windows.EndpointRpc.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(EndpointRpcError))]
[JsonSerializable(typeof(EndpointRecoveryMetadata))]
[JsonSerializable(typeof(EndpointLargeResultDescriptor))]
[JsonSerializable(typeof(GetEndpointLargeResultChunkRequest))]
[JsonSerializable(typeof(GetEndpointLargeResultChunkResponse))]
[JsonSerializable(typeof(ReleaseEndpointLargeResultRequest))]
[JsonSerializable(typeof(ReleaseEndpointLargeResultResponse))]
[JsonSerializable(typeof(RegisterEndpointRequest))]
[JsonSerializable(typeof(LoginEndpointRequest))]
[JsonSerializable(typeof(RenewAuthSessionEndpointRequest))]
[JsonSerializable(typeof(LogoutEndpointRequest))]
[JsonSerializable(typeof(GetAuthSessionStatusEndpointRequest))]
[JsonSerializable(typeof(ChangeMasterPasswordEndpointRequest))]
[JsonSerializable(typeof(GetUserProfileInfoEndpointRequest))]
[JsonSerializable(typeof(DeleteUserAccountEndpointRequest))]
[JsonSerializable(typeof(ChangeUsernameEndpointRequest))]
[JsonSerializable(typeof(UpdateUserProfileInfoEndpointRequest))]
[JsonSerializable(typeof(EmptyEndpointRequest))]
[JsonSerializable(typeof(GetLocalUserSyncOnEndpointRequest))]
[JsonSerializable(typeof(SetLocalUserSyncOnEndpointRequest))]
[JsonSerializable(typeof(SetLocalDeviceNameEndpointRequest))]
[JsonSerializable(typeof(GetUserDevicesEndpointRequest))]
[JsonSerializable(typeof(SetUserDeviceNameEndpointRequest))]
[JsonSerializable(typeof(SetUserDeviceSyncOnEndpointRequest))]
[JsonSerializable(typeof(UnblockUserDeviceEndpointRequest))]
[JsonSerializable(typeof(DisconnectUserDeviceEndpointRequest))]
[JsonSerializable(typeof(AddDeviceByCodeEndpointRequest))]
[JsonSerializable(typeof(InitializeRememberMeSessionEndpointRequest))]
[JsonSerializable(typeof(SetRememberMeEndpointRequest))]
[JsonSerializable(typeof(GetSavedPasswordsEndpointRequest))]
[JsonSerializable(typeof(AddNewPasswordEndpointRequest))]
[JsonSerializable(typeof(RemovePasswordsEndpointRequest))]
[JsonSerializable(typeof(GetUnsecurePasswordEndpointRequest))]
[JsonSerializable(typeof(UpdatePasswordEndpointRequest))]
[JsonSerializable(typeof(ExportPasswordsToUserEndpointRequest))]
[JsonSerializable(typeof(AddCustomUserColorsEndpointRequest))]
[JsonSerializable(typeof(DeleteCustomUserColorsEndpointRequest))]
[JsonSerializable(typeof(ExportCustomUserColorsToUserEndpointRequest))]
[JsonSerializable(typeof(UpdateCustomUserColorEndpointRequest))]
[JsonSerializable(typeof(AddPasswordTagEndpointRequest))]
[JsonSerializable(typeof(DeletePasswordTagEndpointRequest))]
[JsonSerializable(typeof(ExportPasswordTagsToUserEndpointRequest))]
[JsonSerializable(typeof(UpdatePasswordTagEndpointRequest))]
[JsonSerializable(typeof(RunSelfDiagnosticsAndRepairEndpointRequest))]
[JsonSerializable(typeof(RegisterEndpointResponse))]
[JsonSerializable(typeof(LoginEndpointResponse))]
[JsonSerializable(typeof(RenewAuthSessionEndpointResponse))]
[JsonSerializable(typeof(EmptyEndpointResponse))]
[JsonSerializable(typeof(GetAuthSessionStatusEndpointResponse))]
[JsonSerializable(typeof(GetUserProfileInfoEndpointResponse))]
[JsonSerializable(typeof(GetLocalDeviceInfoEndpointResponse))]
[JsonSerializable(typeof(GetLocalUserSyncOnEndpointResponse))]
[JsonSerializable(typeof(GetUserDevicesEndpointResponse))]
[JsonSerializable(typeof(DisconnectUserDeviceEndpointResponse))]
[JsonSerializable(typeof(StartDeviceEnrollmentEndpointResponse))]
[JsonSerializable(typeof(GetDeviceEnrollmentStatusEndpointResponse))]
[JsonSerializable(typeof(RestoreRememberedSessionsEndpointResponse))]
[JsonSerializable(typeof(InitializeRememberMeSessionEndpointResponse))]
[JsonSerializable(typeof(GetSavedPasswordsEndpointResponse))]
[JsonSerializable(typeof(GetUnsecurePasswordEndpointResponse))]
[JsonSerializable(typeof(RunSelfDiagnosticsAndRepairEndpointResponse))]
public partial class EndpointRpcJsonContext : JsonSerializerContext
{
}
