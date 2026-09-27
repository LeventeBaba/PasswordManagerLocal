namespace PasswordManagerLocal.Common.Contracts.Runtime;

public interface IFrontendBackendClient<TEndpoints> : IBackendRuntimeClient
    where TEndpoints : class
{
    Task<TEndpoints> GetEndpointsAsync(CancellationToken cancellationToken = default);
}
