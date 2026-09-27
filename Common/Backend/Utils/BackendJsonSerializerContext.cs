using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Text.Json.Serialization;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Utils;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UserData))]
[JsonSerializable(typeof(SyncVersionStamp))]
[JsonSerializable(typeof(TombstoneCausalReference))]
[JsonSerializable(typeof(GeneralUserData))]
[JsonSerializable(typeof(UserPasswordsData))]
[JsonSerializable(typeof(UserDevicesData))]
[JsonSerializable(typeof(DeletedPasswordData))]
[JsonSerializable(typeof(CustomUserColor))]
[JsonSerializable(typeof(DeletedCustomUserColorData))]
[JsonSerializable(typeof(PasswordTag))]
[JsonSerializable(typeof(DeletedPasswordTagData))]
[JsonSerializable(typeof(DeletedUserDeviceData))]
[JsonSerializable(typeof(DeviceEnrollmentSnapshot), TypeInfoPropertyName = "DeviceEnrollmentSnapshot")]
[JsonSerializable(typeof(SyncDeltaPayload), TypeInfoPropertyName = "SyncDeltaPayload")]
[JsonSerializable(typeof(UserSnapshotEnvelope), TypeInfoPropertyName = "UserSnapshotEnvelope")]
[JsonSerializable(typeof(UserControlOperationEnvelope), TypeInfoPropertyName = "UserControlOperationEnvelope")]
[JsonSerializable(typeof(KeyEpochReplacementPayload), TypeInfoPropertyName = "KeyEpochReplacementPayload")]
[JsonSerializable(typeof(DeviceAdditionPayload), TypeInfoPropertyName = "DeviceAdditionPayload")]
[JsonSerializable(typeof(DeviceRemovalPayload), TypeInfoPropertyName = "DeviceRemovalPayload")]
[JsonSerializable(typeof(AccountDeletionPayload), TypeInfoPropertyName = "AccountDeletionPayload")]
internal partial class BackendJsonSerializerContext : JsonSerializerContext;
