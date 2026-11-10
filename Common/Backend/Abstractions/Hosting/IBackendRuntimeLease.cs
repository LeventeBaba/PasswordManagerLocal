using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Hosting;

public interface IBackendRuntimeLease : IAsyncDisposable
{
    BackendLifetimeReason Reason { get; }
}
