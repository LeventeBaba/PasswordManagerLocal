using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerHost : IWindowsIpcServerHost
{
    private readonly IWindowsIpcConnectionListener _listener;
    private readonly IWindowsIpcServerSessionFactory _sessionFactory;
    private readonly SemaphoreSlim _connectionCapacity;
    private readonly ConcurrentDictionary<IWindowsIpcServerSession, WindowsIpcServerSessionLifetime> _activeSessions = new();
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly object _gate = new();
    private Task? _acceptLoopTask;
    private Task? _stopTask;
    private Exception? _listenerFailure;
    private int _started;

    public WindowsIpcServerHost(
        IWindowsIpcConnectionListener listener,
        IWindowsIpcServerSessionFactory sessionFactory,
        WindowsIpcServerHostOptions? options = null)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        var resolvedOptions = options ?? new WindowsIpcServerHostOptions();
        _connectionCapacity = new SemaphoreSlim(
            resolvedOptions.MaximumActiveConnections,
            resolvedOptions.MaximumActiveConnections);
    }

    public int ActiveSessionCount => _activeSessions.Count;
    public Exception? ListenerFailure => Volatile.Read(ref _listenerFailure);
    public Task Completion => Volatile.Read(ref _acceptLoopTask) ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The IPC server host has already started.");

        _acceptLoopTask = RunAcceptLoopAsync(_shutdownSource.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_gate)
            stopTask = _stopTask ??= StopCoreAsync();

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public Task CloseActiveSessionsAsync(CancellationToken cancellationToken = default) =>
        CloseActiveSessionsCoreAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdownSource.Dispose();
        _connectionCapacity.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await _listener.AcceptAsync(cancellationToken);
                if (!_connectionCapacity.Wait(0))
                {
                    try
                    {
                        await connection.DisposeAsync();
                    }
                    catch
                    {
                    }
                    continue;
                }

                IWindowsIpcServerSession session;
                try
                {
                    session = _sessionFactory.Create(connection);
                }
                catch
                {
                    _connectionCapacity.Release();
                    try
                    {
                        await connection.DisposeAsync();
                    }
                    catch
                    {
                    }
                    continue;
                }

                var lifetime = new WindowsIpcServerSessionLifetime(session);
                if (!_activeSessions.TryAdd(session, lifetime))
                {
                    _connectionCapacity.Release();
                    await lifetime.DisposeAsync();
                    continue;
                }

                lifetime.Start(cancellationToken);
                _ = ObserveSessionAsync(session, lifetime);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _listenerFailure, exception);
            throw;
        }
    }

    private async Task ObserveSessionAsync(
        IWindowsIpcServerSession session,
        WindowsIpcServerSessionLifetime lifetime)
    {
        await lifetime.Completion;
        RemoveSession(session, lifetime);
    }

    private async Task StopCoreAsync()
    {
        _shutdownSource.Cancel();
        var acceptLoop = Completion;
        try
        {
            await acceptLoop;
        }
        catch
        {
        }

        await CloseActiveSessionsCoreAsync(CancellationToken.None);
    }

    private async Task CloseActiveSessionsCoreAsync(CancellationToken cancellationToken)
    {
        while (_activeSessions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _activeSessions.ToArray();
            if (snapshot.Length == 0)
                break;

            foreach (var entry in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await entry.Value.DisposeAsync();
            }

            var completion = Task.WhenAll(snapshot.Select(entry => entry.Value.Completion));
            if (cancellationToken.CanBeCanceled)
                await completion.WaitAsync(cancellationToken);
            else
                await completion;

            foreach (var entry in snapshot)
                RemoveSession(entry.Key, entry.Value);
        }
    }

    private void RemoveSession(
        IWindowsIpcServerSession session,
        WindowsIpcServerSessionLifetime lifetime)
    {
        var entry = new KeyValuePair<IWindowsIpcServerSession, WindowsIpcServerSessionLifetime>(
            session,
            lifetime);
        if (((ICollection<KeyValuePair<IWindowsIpcServerSession, WindowsIpcServerSessionLifetime>>)_activeSessions)
            .Remove(entry))
        {
            _connectionCapacity.Release();
        }
    }
}
