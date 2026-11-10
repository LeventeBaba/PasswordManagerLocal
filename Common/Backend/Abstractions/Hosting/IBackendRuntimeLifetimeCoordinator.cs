using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Hosting;

public interface IBackendRuntimeLifetimeCoordinator
{
    BackendLifetimeReason ActiveReasons { get; }
    IBackendExecutionProfileProvider ExecutionProfileProvider { get; }
    event EventHandler? ActiveReasonsChanged;

    Task<IBackendRuntimeLease> AcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default);
    Task<IBackendRuntimeLease> ResetDatabaseAndAcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default);
    Task RecoverRuntimeAsync(CancellationToken cancellationToken = default);
}
