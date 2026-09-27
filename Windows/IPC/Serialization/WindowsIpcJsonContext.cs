using PasswordManagerLocal.Windows.Ipc.Contracts;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequestEnvelope))]
[JsonSerializable(typeof(IpcResponseEnvelope))]
[JsonSerializable(typeof(IpcHandshakeRequest))]
[JsonSerializable(typeof(IpcHandshakeResponse))]
[JsonSerializable(typeof(WindowsUiIpcIdentity))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(IpcFailureDto))]
[JsonSerializable(typeof(AgentStatusDto))]
[JsonSerializable(typeof(BackendRuntimeStatusDto))]
[JsonSerializable(typeof(DatabaseCompatibilityStatusDto))]
[JsonSerializable(typeof(InteractiveSessionStatusDto))]
[JsonSerializable(typeof(SynchronizationStatusDto))]
[JsonSerializable(typeof(WindowsBackgroundSyncStateDto))]
[JsonSerializable(typeof(SetBackgroundSyncEnabledRequestDto))]
[JsonSerializable(typeof(PingResponseDto))]
[JsonSerializable(typeof(UiConnectionRegistrationResponseDto))]
[JsonSerializable(typeof(UiActivationRequestDto))]
[JsonSerializable(typeof(UiActivationCommand))]
[JsonSerializable(typeof(UiOpenRequestDto))]
[JsonSerializable(typeof(AgentExitRequestDto))]
[JsonSerializable(typeof(RequestAcceptedDto))]
[JsonSerializable(typeof(DatabaseResetResultDto))]
public partial class WindowsIpcJsonContext : JsonSerializerContext
{
}
