using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public sealed class AgentInteractiveEndpointAdapter :
    IEndpointRpcEndpointAdapter,
    IEndpointRpcSessionReadiness,
    IEndpointRpcRestartRequirementHandler,
    IWindowsIpcConnectionLifecycleObserver,
    IAsyncDisposable
{
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _gate = new();
    private Guid? _connectionId;
    private AgentInteractiveBackendBinding? _binding;
    private Exception? _openingFailure;
    private int _disposed;

    public AgentInteractiveEndpointAdapter(IWindowsAgentBackendRuntimeOwner backendOwner) =>
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));

    public bool HasActiveSession
    {
        get
        {
            lock (_gate)
                return _binding is not null;
        }
    }

    public bool AcceptsNewOperations
    {
        get { lock (_gate) return _binding?.AcceptsNewOperations == true; }
    }

    public bool IsClosing
    {
        get { lock (_gate) return _binding?.IsClosing == true; }
    }

    public int ActiveOperationCount
    {
        get { lock (_gate) return _binding?.ActiveOperationCount ?? 0; }
    }

    public Exception? OpeningFailure
    {
        get { lock (_gate) return _openingFailure; }
    }

    public IEndpoints GetEndpoints(EndpointRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            if (_connectionId != context.ConnectionId || _binding is null)
                throw new InvalidOperationException("The endpoint connection has no active interactive backend session.");
            return _binding.Endpoints;
        }
    }

    public bool IsReady(IpcConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
            return _connectionId == connection.ConnectionId && _binding is not null && _openingFailure is null;
    }

    public async ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.State == IpcConnectionLifecycleState.HandshakeCompleted)
        {
            await OpenAsync(notification.ConnectionId, cancellationToken);
            return;
        }

        if (notification.State is IpcConnectionLifecycleState.Disconnected or
            IpcConnectionLifecycleState.Faulted)
        {
            await CloseIfCurrentAsync(notification.ConnectionId);
        }
    }


    public void RequireProcessRestart(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _backendOwner.RequireProcessRestart(failure);
    }

    public Task CloseAllAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;
        return CloseAllCoreAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await CloseAllCoreAsync(CancellationToken.None);
        _transitionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CloseAllCoreAsync(CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            AgentInteractiveBackendBinding? binding;
            lock (_gate)
            {
                binding = _binding;
                _binding = null;
                _connectionId = null;
                _openingFailure = null;
            }

            if (binding is not null)
                await binding.DisposeAsync();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task OpenAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(AgentInteractiveEndpointAdapter));

            AgentInteractiveBackendBinding? replacedBinding;
            lock (_gate)
            {
                if (_connectionId == connectionId && _binding is not null)
                    return;

                replacedBinding = _binding;
                _binding = null;
                _connectionId = connectionId;
                _openingFailure = null;
            }

            if (replacedBinding is not null)
                await replacedBinding.DisposeAsync();

            try
            {
                var binding = await _backendOwner.OpenInteractiveBindingAsync(cancellationToken);
                var accepted = false;
                lock (_gate)
                {
                    if (_connectionId == connectionId)
                    {
                        _binding = binding;
                        accepted = true;
                    }
                }

                if (!accepted)
                {
                    await binding.DisposeAsync();
                    throw new InvalidOperationException("The endpoint connection changed while its session was opening.");
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (_connectionId == connectionId)
                        _openingFailure = exception;
                }
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task CloseIfCurrentAsync(Guid connectionId)
    {
        await _transitionGate.WaitAsync(CancellationToken.None);
        try
        {
            AgentInteractiveBackendBinding? binding;
            lock (_gate)
            {
                if (_connectionId != connectionId)
                    return;
                binding = _binding;
                _binding = null;
                _connectionId = null;
                _openingFailure = null;
            }

            if (binding is not null)
                await binding.DisposeAsync();
        }
        finally
        {
            _transitionGate.Release();
        }
    }
}
