using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentRegisteredConnection : IWindowsAgentRegisteredConnection
{
    private readonly WindowsIpcClient _client;
    private readonly WindowsIpcControlClient _controlClient;
    private int _disposed;

    public WindowsAgentRegisteredConnection(
        WindowsIpcClient client,
        WindowsIpcControlClient controlClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _controlClient = controlClient ?? throw new ArgumentNullException(nameof(controlClient));
    }

    public bool IsConnected => _client.IsConnected && Volatile.Read(ref _disposed) == 0;
    public int? AgentProcessId => _client.VerifiedServerProcessId;
    public Task Completion => _client.Completion;

    public Task<AgentStatusDto> GetAgentStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));
        return _controlClient.GetAgentStatusAsync(cancellationToken);
    }

    public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));
        return _controlClient.GetBackendRuntimeStatusAsync(cancellationToken);
    }

    public Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));
        return _controlClient.GetBackgroundSyncStateAsync(cancellationToken);
    }

    public async Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        SetBackgroundSyncEnabledRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));

        try
        {
            return await _controlClient.SetBackgroundSyncEnabledAsync(
                request,
                cancellationToken);
        }
        catch (IpcRequestTransmissionException exception)
        {
            throw new WindowsAgentControlWriteException(
                MapTransmissionState(exception.TransmissionState),
                exception.InnerException ?? exception);
        }
    }

    public Task<RequestAcceptedDto> ReloadApplicationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));
        return _controlClient.ReloadApplicationPreferencesAsync(cancellationToken);
    }

    public Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentRegisteredConnection));
        return _controlClient.ResetDatabaseAsync(cancellationToken);
    }

    private static WindowsAgentControlWriteTransmissionState MapTransmissionState(
        IpcRequestTransmissionState transmissionState) => transmissionState switch
        {
            IpcRequestTransmissionState.DefinitelyNotSent =>
                WindowsAgentControlWriteTransmissionState.DefinitelyNotSent,
            IpcRequestTransmissionState.Sent =>
                WindowsAgentControlWriteTransmissionState.Sent,
            IpcRequestTransmissionState.TransmissionUnknown =>
                WindowsAgentControlWriteTransmissionState.TransmissionUnknown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(transmissionState),
                transmissionState,
                "The IPC transmission state is invalid.")
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _controlClient.UnregisterUiConnectionAsync();
        }
        catch
        {
        }

        try
        {
            await _client.DisposeAsync();
        }
        catch
        {
        }
    }
}
