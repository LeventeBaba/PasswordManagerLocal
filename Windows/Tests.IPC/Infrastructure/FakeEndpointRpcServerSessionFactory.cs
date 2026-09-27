using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeEndpointRpcServerSessionFactory : IEndpointRpcServerSessionFactory
{
    public int CreateCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int ActiveLargeResultTransferCount { get; set; }
    public Exception? DisposeFailure { get; set; }

    public IEndpointRpcServerSession Create(IWindowsIpcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        CreateCount++;
        throw new NotSupportedException("The endpoint-host lifecycle fake does not create transport sessions.");
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        ActiveLargeResultTransferCount = 0;
        return DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }

    PasswordManagerLocal.Windows.Ipc.Server.IWindowsIpcServerSession
        PasswordManagerLocal.Windows.Ipc.Server.IWindowsIpcServerSessionFactory.Create(
            IWindowsIpcConnection connection) => Create(connection);
}
