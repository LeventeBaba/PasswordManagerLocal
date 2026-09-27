using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public interface IBackendRuntimeLease : IAsyncDisposable
{
    BackendLifetimeReason Reason { get; }
}
