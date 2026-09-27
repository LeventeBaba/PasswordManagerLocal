using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Contracts.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DeviceEnrollmentCodeRequest))]
[JsonSerializable(typeof(ExportCustomUserColorsToUserRequest))]
[JsonSerializable(typeof(ExportPasswordTagsToUserRequest))]
[JsonSerializable(typeof(ExportPasswordsToUserRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(MasterPasswordChangeRequest))]
[JsonSerializable(typeof(NewCustomUserColorRequest))]
[JsonSerializable(typeof(NewPasswordRequest))]
[JsonSerializable(typeof(NewPasswordTagRequest))]
[JsonSerializable(typeof(RegistrationRequest))]
[JsonSerializable(typeof(UpdateCustomUserColorRequest))]
[JsonSerializable(typeof(UpdatePasswordRequest))]
[JsonSerializable(typeof(UpdatePasswordTagRequest))]
[JsonSerializable(typeof(UpdateUserProfileRequest))]
[JsonSerializable(typeof(AuthSessionStatusResponse))]
[JsonSerializable(typeof(CustomUserColorInfoResponse))]
[JsonSerializable(typeof(DeviceEnrollmentCodeResponse))]
[JsonSerializable(typeof(DeviceEnrollmentInfoResponse))]
[JsonSerializable(typeof(DeviceEnrollmentStatusResponse))]
[JsonSerializable(typeof(DeviceRemovalResultResponse))]
[JsonSerializable(typeof(LocalDeviceInfoResponse))]
[JsonSerializable(typeof(PasswordInfoResponse))]
[JsonSerializable(typeof(PasswordTagInfoResponse))]
[JsonSerializable(typeof(SavedPasswordsResponse))]
[JsonSerializable(typeof(UserDeviceInfoResponse))]
[JsonSerializable(typeof(UserProfileInfoResponse))]
public partial class EndpointContractsJsonContext : JsonSerializerContext;
