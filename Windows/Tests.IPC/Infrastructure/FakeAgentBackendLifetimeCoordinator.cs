using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAgentBackendLifetimeCoordinator : IBackendRuntimeLifetimeCoordinator
{
    public IBackendExecutionProfileProvider ExecutionProfileProvider { get; } =
        new FakeBackendExecutionProfileProvider();

    public BackendLifetimeReason ActiveReasons { get; set; }
    public event EventHandler? ActiveReasonsChanged;

    public void PublishActiveReasonsChanged() =>
        ActiveReasonsChanged?.Invoke(this, EventArgs.Empty);

    public Task<IBackendRuntimeLease> AcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IBackendRuntimeLease>(
            new NotSupportedException("This fake is used for owner lifecycle tests."));

    public Task<IBackendRuntimeLease> ResetDatabaseAndAcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IBackendRuntimeLease>(
            new NotSupportedException("This fake is used for owner lifecycle tests."));

    public Task RecoverRuntimeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
