using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentControlConnector : IWindowsAgentControlConnector
{
    private readonly WindowsAgentHealthValidator _healthValidator = new();

    public async Task<WindowsAgentControlConnectionAttempt> TryConnectAndRegisterAsync(
        string pipeName,
        WindowsUiIpcIdentity identity,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(identity);
        if (connectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));

        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();
        using var timeoutSource = new CancellationTokenSource(connectTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        IWindowsIpcConnection? connection = null;
        WindowsIpcClient? client = null;
        int? agentProcessId = null;
        try
        {
            connection = await new WindowsNamedPipeClient(
                pipeName,
                new IpcFrameCodec()).ConnectAsync(linkedSource.Token);
            client = new WindowsIpcClient(
                connection,
                serializer,
                new WindowsIpcClientOptions(
                    IpcPeerRole.Ui,
                    IpcPeerRole.Agent,
                    IpcCapabilities.Control | IpcCapabilities.Status | IpcCapabilities.UiActivation,
                    identity.ProcessId,
                    identity.WindowsSessionId,
                    identity.InstanceId,
                    maximumPendingRequests: 16),
                validator);
            connection = null;
            await client.HandshakeAsync(linkedSource.Token);
            agentProcessId = client.VerifiedServerProcessId;

            var controlClient = new WindowsIpcControlClient(client, serializer, validator);
            var agentStatus = await controlClient.GetAgentStatusAsync(linkedSource.Token);
            var backendStatus = await controlClient.GetBackendRuntimeStatusAsync(linkedSource.Token);
            if (!_healthValidator.CanRegisterUi(agentStatus, backendStatus))
                return new WindowsAgentControlConnectionAttempt(null, agentProcessId);

            var registration = await controlClient.RegisterUiConnectionAsync(linkedSource.Token);
            if (!registration.IsRegistered)
                return new WindowsAgentControlConnectionAttempt(null, agentProcessId);

            var registered = new WindowsAgentRegisteredConnection(client, controlClient);
            client = null;
            return new WindowsAgentControlConnectionAttempt(registered, agentProcessId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new WindowsAgentControlConnectionAttempt(null, agentProcessId);
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync(); } catch { }
            }
            else if (connection is not null)
            {
                try { await connection.DisposeAsync(); } catch { }
            }
        }
    }
}
