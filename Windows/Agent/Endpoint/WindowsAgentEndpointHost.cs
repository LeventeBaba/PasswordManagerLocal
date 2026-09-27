using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public sealed class WindowsAgentEndpointHost : IWindowsAgentEndpointHost
{
    public const int MaximumActiveEndpointConnections = 1;

    private readonly string _pipeName;
    private readonly AgentInteractiveEndpointAdapter _endpointAdapter;
    private readonly RegisteredUiEndpointRegistrationResolver _registrationResolver;
    private readonly IEndpointRpcAdmissionPolicy _admissionPolicy;
    private readonly Func<
        AgentInteractiveEndpointAdapter,
        RegisteredUiEndpointRegistrationResolver,
        IEndpointRpcAdmissionPolicy,
        IEndpointRpcServerSessionFactory> _sessionFactoryFactory;
    private readonly Func<
        string,
        IWindowsIpcServerSessionFactory,
        IWindowsIpcServerHost> _hostFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly object _registrationTaskGate = new();
    private IWindowsIpcServerHost? _host;
    private IEndpointRpcServerSessionFactory? _sessionFactory;
    private Task? _observerTask;
    private Task _registrationChangeTask = Task.CompletedTask;
    private int _disposed;
    private WindowsAgentEndpointHostSnapshot _snapshot = new(
        WindowsAgentEndpointHostState.Stopped,
        null,
        DateTimeOffset.UtcNow);

    public WindowsAgentEndpointHost(
        string pipeName,
        AgentInteractiveEndpointAdapter endpointAdapter,
        RegisteredUiEndpointRegistrationResolver registrationResolver,
        IWindowsAgentAdmissionGate admissionGate,
        IWindowsAgentStateSource stateSource,
        IWindowsAgentBackendRuntimeOwner backendOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _endpointAdapter = endpointAdapter ?? throw new ArgumentNullException(nameof(endpointAdapter));
        _registrationResolver = registrationResolver
            ?? throw new ArgumentNullException(nameof(registrationResolver));
        ArgumentNullException.ThrowIfNull(admissionGate);
        ArgumentNullException.ThrowIfNull(stateSource);
        ArgumentNullException.ThrowIfNull(backendOwner);
        _admissionPolicy = new WindowsAgentEndpointAdmissionPolicy(
            admissionGate,
            stateSource,
            backendOwner,
            () => Snapshot.State);
        _sessionFactoryFactory = (adapter, resolver, policy) =>
        {
            var connectionAuthorizer = new EndpointRpcConnectionAuthorizer(resolver, policy);
            return new EndpointRpcServerSessionFactory(
                adapter,
                connectionAuthorizer,
                policy,
                adapter,
                [adapter]);
        };
        _hostFactory = (name, sessionFactory) => new WindowsIpcServerHost(
            new WindowsNamedPipeServer(name, new IpcFrameCodec()),
            sessionFactory,
            new WindowsIpcServerHostOptions(MaximumActiveEndpointConnections));
        _registrationResolver.RegistrationChanged += HandleRegistrationChanged;
    }

    internal WindowsAgentEndpointHost(
        string pipeName,
        AgentInteractiveEndpointAdapter endpointAdapter,
        RegisteredUiEndpointRegistrationResolver registrationResolver,
        IEndpointRpcAdmissionPolicy admissionPolicy,
        Func<
            AgentInteractiveEndpointAdapter,
            RegisteredUiEndpointRegistrationResolver,
            IEndpointRpcAdmissionPolicy,
            IEndpointRpcServerSessionFactory> sessionFactoryFactory,
        Func<
            string,
            IWindowsIpcServerSessionFactory,
            IWindowsIpcServerHost> hostFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _endpointAdapter = endpointAdapter ?? throw new ArgumentNullException(nameof(endpointAdapter));
        _registrationResolver = registrationResolver
            ?? throw new ArgumentNullException(nameof(registrationResolver));
        _admissionPolicy = admissionPolicy ?? throw new ArgumentNullException(nameof(admissionPolicy));
        _sessionFactoryFactory = sessionFactoryFactory
            ?? throw new ArgumentNullException(nameof(sessionFactoryFactory));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _registrationResolver.RegistrationChanged += HandleRegistrationChanged;
    }

    public WindowsAgentEndpointHostSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
                return _snapshot;
        }
    }

    public Task Completion => _host?.Completion ?? Task.CompletedTask;

    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_host is not null)
                return;

            Publish(WindowsAgentEndpointHostState.Starting, null);
            IEndpointRpcServerSessionFactory? sessionFactory = null;
            IWindowsIpcServerHost? host = null;
            try
            {
                sessionFactory = _sessionFactoryFactory(
                    _endpointAdapter,
                    _registrationResolver,
                    _admissionPolicy)
                    ?? throw new InvalidOperationException("The endpoint session factory returned null.");
                host = _hostFactory(_pipeName, sessionFactory)
                    ?? throw new InvalidOperationException("The endpoint server host factory returned null.");
                await host.StartAsync(cancellationToken);
                _sessionFactory = sessionFactory;
                _host = host;
                _observerTask = ObserveHostAsync(host);
                Publish(WindowsAgentEndpointHostState.Ready, null);
            }
            catch (Exception exception)
            {
                Exception failure = exception;
                if (host is not null)
                {
                    try { await host.DisposeAsync(); }
                    catch (Exception cleanupFailure) { failure = Combine(failure, cleanupFailure); }
                }
                if (sessionFactory is not null)
                {
                    try { await sessionFactory.DisposeAsync(); }
                    catch (Exception cleanupFailure) { failure = Combine(failure, cleanupFailure); }
                }
                Publish(WindowsAgentEndpointHostState.Failed, failure);
                throw failure;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var host = _host;
            var sessionFactory = _sessionFactory;
            var observerTask = _observerTask;
            _host = null;
            _sessionFactory = null;
            _observerTask = null;
            if (host is null && sessionFactory is null)
            {
                if (Snapshot.State != WindowsAgentEndpointHostState.Failed)
                    Publish(WindowsAgentEndpointHostState.Stopped, null);
                return;
            }

            Publish(WindowsAgentEndpointHostState.Stopping, null);
            Exception? failure = null;
            try { await _admissionPolicy.WaitForDrainAsync(CancellationToken.None); }
            catch (Exception exception) { failure = exception; }
            if (host is not null)
            {
                try { await host.StopAsync(cancellationToken); }
                catch (Exception exception) { failure = Combine(failure, exception); }
                try { await host.DisposeAsync(); }
                catch (Exception exception) { failure = Combine(failure, exception); }
            }
            if (observerTask is not null)
            {
                try { await observerTask; }
                catch (Exception exception) { failure = Combine(failure, exception); }
            }
            try { await _endpointAdapter.CloseAllAsync(CancellationToken.None); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            if (sessionFactory is not null)
            {
                try { await sessionFactory.DisposeAsync(); }
                catch (Exception exception) { failure = Combine(failure, exception); }
            }

            Publish(
                failure is null ? WindowsAgentEndpointHostState.Stopped : WindowsAgentEndpointHostState.Failed,
                failure);
            if (failure is not null)
                throw failure;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _registrationResolver.RegistrationChanged -= HandleRegistrationChanged;
        Exception? failure = null;
        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Task registrationChangeTask;
        lock (_registrationTaskGate)
            registrationChangeTask = _registrationChangeTask;
        try
        {
            await registrationChangeTask;
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        try
        {
            await _endpointAdapter.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        _registrationResolver.Dispose();
        _lifecycleGate.Dispose();
        StateChanged = null;
        GC.SuppressFinalize(this);
        if (failure is not null)
            throw failure;
    }

    private void HandleRegistrationChanged(
        object? sender,
        UiConnectionRegistrationChangedEventArgs args)
    {
        if (args.Previous is null ||
            args.Previous.ConnectionId == args.Current?.ConnectionId ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_registrationTaskGate)
            _registrationChangeTask = InvalidateSessionsAfterAsync(_registrationChangeTask);
    }

    private async Task InvalidateSessionsAfterAsync(Task previous)
    {
        try { await previous; } catch { }

        var host = Volatile.Read(ref _host);
        if (host is null || Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            await host.CloseActiveSessionsAsync(CancellationToken.None);
            await _endpointAdapter.CloseAllAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                Snapshot.State is not WindowsAgentEndpointHostState.Stopping and
                    not WindowsAgentEndpointHostState.Stopped)
            {
                Publish(WindowsAgentEndpointHostState.Failed, exception);
            }
            throw;
        }
    }

    private async Task ObserveHostAsync(IWindowsIpcServerHost host)
    {
        try
        {
            await host.Completion;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_host, host))
                Publish(WindowsAgentEndpointHostState.Failed, exception);
        }

        if (ReferenceEquals(_host, host) && host.ListenerFailure is { } listenerFailure)
            Publish(WindowsAgentEndpointHostState.Failed, listenerFailure);
    }

    private void Publish(WindowsAgentEndpointHostState state, Exception? failure)
    {
        lock (_snapshotGate)
        {
            _snapshot = new WindowsAgentEndpointHostSnapshot(
                state,
                failure,
                DateTimeOffset.UtcNow);
        }

        var handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentEndpointHost));
    }

    private Exception Combine(Exception? first, Exception second) =>
        first is null ? second : new AggregateException(first, second);
}
