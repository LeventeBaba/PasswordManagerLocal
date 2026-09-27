using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Windows.Agent.Backend;

public sealed class AgentInteractiveBackendBinding : IAsyncDisposable
{
    private readonly IInteractiveBackendSession _session;
    private readonly IBackendRuntimeLease _lease;
    private readonly Func<ValueTask> _released;
    private int _disposed;

    public AgentInteractiveBackendBinding(
        IInteractiveBackendSession session,
        IBackendRuntimeLease lease,
        Func<ValueTask> released)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _released = released ?? throw new ArgumentNullException(nameof(released));
    }

    public IEndpoints Endpoints => _session.Endpoints;
    public bool AcceptsNewOperations => _session.AcceptsNewOperations;
    public bool IsClosing => _session.IsClosing;
    public int ActiveOperationCount => _session.ActiveOperationCount;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await _lease.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        try
        {
            await _released();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        GC.SuppressFinalize(this);
        if (failure is not null)
            throw failure;
    }
}
