using PasswordManagerLocal.Common.Contracts.Endpoints;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public interface IInteractiveBackendSession : IAsyncDisposable
{
    IEndpoints Endpoints { get; }
    bool AcceptsNewOperations { get; }
    bool IsClosing { get; }
    int ActiveOperationCount { get; }
}
