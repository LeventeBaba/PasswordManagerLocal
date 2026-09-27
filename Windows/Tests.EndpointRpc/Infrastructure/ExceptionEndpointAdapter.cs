using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.EndpointRpc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class ExceptionEndpointAdapter : IEndpointRpcEndpointAdapter
{
    private readonly Exception _exception;

    public ExceptionEndpointAdapter(Exception exception) =>
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));

    public EndpointRequestContext? LastContext { get; private set; }

    public IEndpoints GetEndpoints(EndpointRequestContext context)
    {
        LastContext = context ?? throw new ArgumentNullException(nameof(context));
        throw _exception;
    }
}
