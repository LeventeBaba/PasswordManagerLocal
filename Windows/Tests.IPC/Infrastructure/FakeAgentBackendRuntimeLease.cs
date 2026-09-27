using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAgentBackendRuntimeLease : IBackendRuntimeLease
{
    private readonly ICollection<string>? _operationLog;
    private readonly Func<ValueTask>? _onDispose;
    private int _disposed;

    public FakeAgentBackendRuntimeLease(
        BackendLifetimeReason reason,
        ICollection<string>? operationLog = null,
        Func<ValueTask>? onDispose = null)
    {
        Reason = reason;
        _operationLog = operationLog;
        _onDispose = onDispose;
    }

    public BackendLifetimeReason Reason { get; }
    public int DisposeCount { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DisposeCount++;
        _operationLog?.Add("lease-dispose");
        if (_onDispose is not null)
            await _onDispose();
    }
}
