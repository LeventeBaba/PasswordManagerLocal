using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointRpcServerSession : IEndpointRpcServerSession
{
    private readonly IWindowsIpcServerSession _innerSession;

    public EndpointRpcServerSession(IWindowsIpcServerSession innerSession) =>
        _innerSession = innerSession ?? throw new ArgumentNullException(nameof(innerSession));

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _innerSession.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() => _innerSession.DisposeAsync();
}
