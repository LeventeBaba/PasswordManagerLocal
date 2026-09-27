using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class WindowsIpcControlClient
{
    private readonly WindowsIpcClient _client;
    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcContractValidator _contractValidator;

    public WindowsIpcControlClient(
        WindowsIpcClient client,
        WindowsIpcSerializer serializer,
        WindowsIpcContractValidator? contractValidator = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
    }

    public Task<PingResponseDto> PingAsync(CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.Ping,
            WindowsIpcJsonContext.Default.PingResponseDto,
            cancellationToken);

    public Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.GetAgentStatus,
            WindowsIpcJsonContext.Default.AgentStatusDto,
            cancellationToken);

    public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.GetBackendRuntimeStatus,
            WindowsIpcJsonContext.Default.BackendRuntimeStatusDto,
            cancellationToken);

    public Task<InteractiveSessionStatusDto> GetInteractiveSessionStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.GetInteractiveSessionStatus,
            WindowsIpcJsonContext.Default.InteractiveSessionStatusDto,
            cancellationToken);

    public Task<SynchronizationStatusDto> GetSynchronizationStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.GetSynchronizationStatus,
            WindowsIpcJsonContext.Default.SynchronizationStatusDto,
            cancellationToken);

    public Task<UiConnectionRegistrationResponseDto> RegisterUiConnectionAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.RegisterUiConnection,
            WindowsIpcJsonContext.Default.UiConnectionRegistrationResponseDto,
            cancellationToken);

    public Task<UiConnectionRegistrationResponseDto> UnregisterUiConnectionAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.UnregisterUiConnection,
            WindowsIpcJsonContext.Default.UiConnectionRegistrationResponseDto,
            cancellationToken);

    public Task<RequestAcceptedDto> RequestUiOpenAsync(
        UiOpenRequestDto request,
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.RequestUiOpen,
            request,
            WindowsIpcJsonContext.Default.UiOpenRequestDto,
            WindowsIpcJsonContext.Default.RequestAcceptedDto,
            cancellationToken);

    public Task<RequestAcceptedDto> RequestUiActivationAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.RequestUiActivation,
            request,
            WindowsIpcJsonContext.Default.UiActivationRequestDto,
            WindowsIpcJsonContext.Default.RequestAcceptedDto,
            cancellationToken);

    public Task<RequestAcceptedDto> RequestAgentExitAsync(
        AgentExitRequestDto request,
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.RequestAgentExit,
            request,
            WindowsIpcJsonContext.Default.AgentExitRequestDto,
            WindowsIpcJsonContext.Default.RequestAcceptedDto,
            cancellationToken);

    public Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.ResetDatabase,
            WindowsIpcJsonContext.Default.DatabaseResetResultDto,
            cancellationToken);

    public Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.GetBackgroundSyncState,
            WindowsIpcJsonContext.Default.WindowsBackgroundSyncStateDto,
            cancellationToken);

    public Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        SetBackgroundSyncEnabledRequestDto request,
        CancellationToken cancellationToken = default) =>
        SendTransmissionAwareForResultAsync(
            IpcOperationId.SetBackgroundSyncEnabled,
            request,
            WindowsIpcJsonContext.Default.SetBackgroundSyncEnabledRequestDto,
            WindowsIpcJsonContext.Default.WindowsBackgroundSyncStateDto,
            cancellationToken);

    public Task<RequestAcceptedDto> ReloadApplicationPreferencesAsync(
        CancellationToken cancellationToken = default) =>
        SendForResultAsync(
            IpcOperationId.ReloadApplicationPreferences,
            WindowsIpcJsonContext.Default.RequestAcceptedDto,
            cancellationToken);

    private async Task<TResponse> SendForResultAsync<TResponse>(
        IpcOperationId operationId,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(operationId, null, cancellationToken);
        return DeserializeRequiredResult(response, responseTypeInfo);
    }

    private async Task<TResponse> SendForResultAsync<TRequest, TResponse>(
        IpcOperationId operationId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _contractValidator.ValidateForTransport(request);
        var payload = _serializer.Serialize(request, requestTypeInfo);
        var response = await _client.SendAsync(operationId, payload, cancellationToken);
        return DeserializeRequiredResult(response, responseTypeInfo);
    }

    private async Task<TResponse> SendTransmissionAwareForResultAsync<TRequest, TResponse>(
        IpcOperationId operationId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _contractValidator.ValidateForTransport(request);
        var payload = _serializer.Serialize(request, requestTypeInfo);
        var response = await _client.SendWithTransmissionStateAsync(
            operationId,
            payload,
            cancellationToken);
        return DeserializeRequiredResult(response.Response, responseTypeInfo);
    }

    private TResponse DeserializeRequiredResult<TResponse>(
        IpcResponseEnvelope response,
        JsonTypeInfo<TResponse> responseTypeInfo)
    {
        if (response.Result is null)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC response result is missing.");
        }

        try
        {
            var result = _serializer.Deserialize(response.Result, responseTypeInfo);
            _contractValidator.ValidateForTransport(result);
            return result;
        }
        catch (IpcPayloadException exception)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC response result is semantically invalid.",
                exception);
        }
    }
}
