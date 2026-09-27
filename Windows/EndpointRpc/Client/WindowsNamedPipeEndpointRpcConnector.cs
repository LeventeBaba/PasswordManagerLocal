using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class WindowsNamedPipeEndpointRpcConnector : IEndpointRpcClientConnector
{
    public const int MaximumPendingEndpointRequests = 32;

    private readonly string _pipeName;
    private readonly WindowsUiIpcIdentity _identity;

    public WindowsNamedPipeEndpointRpcConnector(
        string pipeName,
        WindowsUiIpcIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public async Task<IEndpointRpcTransport> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var pipeClient = new WindowsNamedPipeClient(
            _pipeName,
            new IpcFrameCodec());
        var connection = await pipeClient.ConnectAsync(cancellationToken);
        WindowsIpcClient? client = null;
        try
        {
            client = new WindowsIpcClient(
                connection,
                new WindowsIpcSerializer(),
                new WindowsIpcClientOptions(
                    IpcPeerRole.Ui,
                    IpcPeerRole.Agent,
                    IpcCapabilities.EndpointRpc,
                    _identity.ProcessId,
                    _identity.WindowsSessionId,
                    _identity.InstanceId,
                    MaximumPendingEndpointRequests,
                    requiredServerCapabilities: IpcCapabilities.EndpointRpc));
            await client.HandshakeAsync(cancellationToken);
            var readinessResponse = await client.SendAsync(
                IpcOperationId.EndpointSessionReady,
                cancellationToken: cancellationToken);
            if (!readinessResponse.IsSuccess)
                throw new IpcRemoteException(readinessResponse.Error!);
            var readiness = new WindowsIpcSerializer().Deserialize(
                readinessResponse.Result!,
                PasswordManagerLocal.Windows.Ipc.Serialization.WindowsIpcJsonContext.Default.RequestAcceptedDto);
            if (!readiness.Accepted)
                throw new InvalidOperationException("The agent backend session is not ready.");
            return new WindowsEndpointRpcTransport(
                client,
                new EndpointRpcMessageCodec(new EndpointRpcSerializer()));
        }
        catch (Exception exception)
        {
            var connectionFailure = exception is IpcRemoteException remoteException
                ? EndpointRpcTransportErrorMapper.Map(remoteException.Error)
                : exception;
            Exception? disposalFailure = null;
            try
            {
                if (client is not null)
                    await client.DisposeAsync();
                else
                    await connection.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                disposalFailure = cleanupException;
            }

            if (disposalFailure is not null)
                throw new AggregateException(connectionFailure, disposalFailure);
            if (!ReferenceEquals(connectionFailure, exception))
                throw connectionFailure;
            throw;
        }
    }
}
