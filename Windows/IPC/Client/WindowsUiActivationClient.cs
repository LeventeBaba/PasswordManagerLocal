using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.IO.Pipes;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class WindowsUiActivationClient : IWindowsUiActivationClient
{
    private readonly string _pipeName;
    private readonly IpcPeerRole _clientRole;
    private readonly TimeSpan _connectionTimeout;

    public WindowsUiActivationClient(
        string pipeName,
        IpcPeerRole clientRole,
        TimeSpan? connectionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (clientRole is not IpcPeerRole.Agent and not IpcPeerRole.Ui and not IpcPeerRole.TestClient)
            throw new ArgumentOutOfRangeException(nameof(clientRole));

        _pipeName = pipeName;
        _clientRole = clientRole;
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromMilliseconds(750);
        if (_connectionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
    }

    public async Task<UiActivationResult> TryActivateAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();
        validator.Validate(request);
        using var timeoutSource = new CancellationTokenSource(_connectionTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        IWindowsIpcConnection? connection = null;
        WindowsIpcClient? client = null;
        var connectionEstablished = false;
        try
        {
            connection = await new WindowsNamedPipeClient(
                _pipeName,
                new IpcFrameCodec()).ConnectAsync(linkedSource.Token);
            connectionEstablished = true;
            client = new WindowsIpcClient(
                connection,
                serializer,
                new WindowsIpcClientOptions(
                    _clientRole,
                    IpcPeerRole.Ui,
                    IpcCapabilities.UiActivation,
                    Environment.ProcessId,
                    System.Diagnostics.Process.GetCurrentProcess().SessionId,
                    Guid.NewGuid(),
                    maximumPendingRequests: 2),
                validator);
            connection = null;
            await client.HandshakeAsync(linkedSource.Token);
            var response = await new WindowsIpcControlClient(client, serializer, validator)
                .RequestUiActivationAsync(request, linkedSource.Token);
            return response.Accepted
                ? new UiActivationResult(
                    UiActivationResultKind.Activated,
                    "The existing UI accepted the activation request.")
                : new UiActivationResult(
                    UiActivationResultKind.Rejected,
                    "The existing UI rejected the activation request.");
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return connectionEstablished
                ? new UiActivationResult(
                    UiActivationResultKind.Failed,
                    "The UI activation attempt timed out after connecting.")
                : new UiActivationResult(
                    UiActivationResultKind.Unavailable,
                    "The UI activation server is unavailable.");
        }
        catch (IOException)
        {
            return connectionEstablished
                ? new UiActivationResult(
                    UiActivationResultKind.Failed,
                    "The UI activation connection failed after connecting.")
                : new UiActivationResult(
                    UiActivationResultKind.Unavailable,
                    "The UI activation server is unavailable.");
        }
        catch (TimeoutException)
        {
            return connectionEstablished
                ? new UiActivationResult(
                    UiActivationResultKind.Failed,
                    "The UI activation attempt failed after connecting.")
                : new UiActivationResult(
                    UiActivationResultKind.Unavailable,
                    "The UI activation server is unavailable.");
        }
        catch (IpcRemoteException)
        {
            return new UiActivationResult(
                UiActivationResultKind.Rejected,
                "The UI activation request was rejected.");
        }
        catch (IpcProtocolException)
        {
            return new UiActivationResult(
                UiActivationResultKind.Failed,
                "The UI activation protocol failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new UiActivationResult(
                UiActivationResultKind.Failed,
                "The UI activation attempt failed.");
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch
                {
                }
            }
            else if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                }
            }
        }
    }
}
