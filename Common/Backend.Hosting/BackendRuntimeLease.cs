using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal sealed class BackendRuntimeLease : IBackendRuntimeLease
{
    private BackendRuntimeLifetimeCoordinator? _owner;

    public BackendRuntimeLease(
        BackendRuntimeLifetimeCoordinator owner,
        BackendLifetimeReason reason)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Reason = reason;
    }

    public BackendLifetimeReason Reason { get; }

    public async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;

        await owner.ReleaseAsync(Reason);
        GC.SuppressFinalize(this);
    }
}
