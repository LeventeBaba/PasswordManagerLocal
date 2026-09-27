using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentControlConnection : IWindowsAgentControlConnection
{
    private readonly string _pipeName;
    private readonly WindowsUiIpcIdentity _identity;
    private readonly IWindowsAgentLauncher _agentLauncher;
    private readonly IWindowsAgentControlConnector _connector;
    private readonly int _maximumConnectionAttempts;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _replacementExitTimeout;
    private readonly IWindowsAgentProcessExitWaiter _processExitWaiter;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IWindowsAgentRegisteredConnection? _connection;
    private int? _lastAgentProcessId;
    private long _connectionGeneration;
    private int _disposed;

    public WindowsAgentControlConnection(
        string pipeName,
        WindowsUiIpcIdentity identity,
        IWindowsAgentLauncher agentLauncher,
        IWindowsAgentControlConnector? connector = null,
        int maximumConnectionAttempts = 20,
        TimeSpan? connectTimeout = null,
        TimeSpan? retryDelay = null,
        TimeSpan? replacementExitTimeout = null,
        IWindowsAgentProcessExitWaiter? processExitWaiter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _agentLauncher = agentLauncher ?? throw new ArgumentNullException(nameof(agentLauncher));
        _connector = connector ?? new WindowsAgentControlConnector();
        if (maximumConnectionAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConnectionAttempts));
        _maximumConnectionAttempts = maximumConnectionAttempts;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        _replacementExitTimeout = replacementExitTimeout ?? TimeSpan.FromSeconds(10);
        _processExitWaiter = processExitWaiter ?? new WindowsAgentProcessExitWaiter();
        if (_connectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        if (_retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (_replacementExitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(replacementExitTimeout));
    }

    public bool IsConnected => _connection?.IsConnected == true;
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);
    public Task Completion => _connection?.Completion ?? Task.CompletedTask;
    public int? AgentProcessId => _connection?.AgentProcessId ?? _lastAgentProcessId;

    public Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(cancellationToken);

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (IsConnected)
                return true;

            await DisposeCurrentConnectionAsync();
            if (await TryConnectAndRegisterAsync(cancellationToken))
                return true;

            var observedProcessCount = 0;
            while (_lastAgentProcessId.HasValue &&
                   observedProcessCount < _maximumConnectionAttempts)
            {
                observedProcessCount++;
                var observedProcessId = _lastAgentProcessId.Value;
                using var exitObservationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var exitObservation = _processExitWaiter.WaitForExitAsync(
                    observedProcessId,
                    _replacementExitTimeout,
                    exitObservationSource.Token);

                for (var attempt = 0;
                    attempt < _maximumConnectionAttempts && !exitObservation.IsCompleted;
                    attempt++)
                {
                    if (_retryDelay > TimeSpan.Zero)
                        await Task.Delay(_retryDelay, cancellationToken);
                    if (await TryConnectAndRegisterAsync(cancellationToken))
                    {
                        await CancelExitObservationAsync(
                            exitObservationSource,
                            exitObservation,
                            cancellationToken);
                        return true;
                    }
                }

                if (!await exitObservation)
                {
                    if (await TryConnectAndRegisterAsync(cancellationToken))
                        return true;
                    return false;
                }

                if (_lastAgentProcessId == observedProcessId)
                    _lastAgentProcessId = null;
                if (await TryConnectAndRegisterAsync(cancellationToken))
                    return true;
            }

            if (_lastAgentProcessId.HasValue)
                return false;

            if (!await _agentLauncher.LaunchAsync(cancellationToken))
                return false;

            for (var attempt = 0; attempt < _maximumConnectionAttempts; attempt++)
            {
                if (attempt > 0 && _retryDelay > TimeSpan.Zero)
                    await Task.Delay(_retryDelay, cancellationToken);
                if (await TryConnectAndRegisterAsync(cancellationToken))
                    return true;
            }

            return false;
        }
        finally
        {
            _connectionGate.Release();
        }
    }


    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await DisposeCurrentConnectionAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> PrepareForReplacementAsync(
        CancellationToken cancellationToken = default)
    {
        Task<bool> exitTask = Task.FromResult(true);
        int? agentProcessId = null;
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            agentProcessId = _connection?.AgentProcessId ?? _lastAgentProcessId;
            if (agentProcessId.HasValue)
            {
                exitTask = _processExitWaiter.WaitForExitAsync(
                    agentProcessId.Value,
                    _replacementExitTimeout,
                    cancellationToken);
            }
            await DisposeCurrentConnectionAsync();
        }
        finally
        {
            _connectionGate.Release();
        }

        var exited = await exitTask;
        if (exited && agentProcessId.HasValue)
        {
            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                if (_lastAgentProcessId == agentProcessId)
                    _lastAgentProcessId = null;
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        return exited;
    }

    public async Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("The Windows agent control connection is unavailable.");
            return await _connection.GetBackendRuntimeStatusAsync(cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AgentStatusDto> GetAgentStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("The Windows agent control connection is unavailable.");
            return await _connection.GetAgentStatusAsync(cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("The Windows agent control connection is unavailable.");
            return await _connection.GetBackgroundSyncStateAsync(cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("The Windows agent control connection is unavailable.");
            return await _connection.SetBackgroundSyncEnabledAsync(
                new SetBackgroundSyncEnabledRequestDto(isEnabled),
                cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> ReloadApplicationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                return false;
            var response = await _connection.ReloadApplicationPreferencesAsync(cancellationToken)
                .ConfigureAwait(false);
            return response.Accepted;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("The Windows agent control connection is unavailable.");
            return await _connection.ResetDatabaseAsync(cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _connectionGate.WaitAsync();
        try
        {
            await DisposeCurrentConnectionAsync();
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private async Task<bool> TryConnectAndRegisterAsync(
        CancellationToken cancellationToken)
    {
        var attempt = await _connector.TryConnectAndRegisterAsync(
            _pipeName,
            _identity,
            _connectTimeout,
            cancellationToken);
        if (attempt.AgentProcessId.HasValue)
            _lastAgentProcessId = attempt.AgentProcessId;
        _connection = attempt.Connection;
        if (_connection is null)
            return false;

        _lastAgentProcessId = _connection.AgentProcessId;
        Interlocked.Increment(ref _connectionGeneration);
        return true;
    }

    private static async Task CancelExitObservationAsync(
        CancellationTokenSource? source,
        Task<bool>? observation,
        CancellationToken cancellationToken)
    {
        if (source is null || observation is null)
            return;

        source.Cancel();
        try
        {
            await observation;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task DisposeCurrentConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
            return;
        if (connection.AgentProcessId.HasValue)
            _lastAgentProcessId = connection.AgentProcessId;
        try { await connection.DisposeAsync(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentControlConnection));
    }
}
