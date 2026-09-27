using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using PasswordManagerLocal.Common.Backend.Abstractions.State;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class TcpSyncServerHostedService : ISyncControlledHostedService
{
    private readonly IDeviceIdentityService _identity;
    private readonly SyncPeerProtocolHandler _handler;
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private readonly IBackendExecutionProfileProvider? _executionProfileProvider;
    private readonly SemaphoreSlim _connectionSlots = new(SyncConstants.MaxConcurrentSyncConnections, SyncConstants.MaxConcurrentSyncConnections);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _connectionsByRemoteIp = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public TcpSyncServerHostedService(
        IDeviceIdentityService identity,
        SyncPeerProtocolHandler handler,
        IEnrollmentRuntimeState enrollmentState,
        IBackendExecutionProfileProvider? executionProfileProvider = null)
    {
        _identity = identity;
        _handler = handler;
        _enrollmentState = enrollmentState;
        _executionProfileProvider = executionProfileProvider;
    }




    public int StartOrder => 20;




    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!_identity.IsSyncOn && !_enrollmentState.IsActive)
            {
                BackendDebugLog.Info("TCP sync server was not started because synchronization is disabled on this device.");
                return;
            }

            if (_listener is not null)
                return;

            var listener = new TcpListener(IPAddress.Any, SyncConstants.SyncPort)
            {
                ExclusiveAddressUse = true
            };
            var lifetime = new CancellationTokenSource();

            try
            {
                listener.Start();
                _listener = listener;
                _cts = lifetime;
                _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, lifetime.Token), CancellationToken.None);
                var localEndpoint = listener.LocalEndpoint?.ToString() ?? $"0.0.0.0:{SyncConstants.SyncPort}";
                BackendDebugLog.Info($"TCP sync server listening on {localEndpoint}. ProcessId={Environment.ProcessId}, ExclusiveAddressUse={listener.ExclusiveAddressUse}.");
                if (_identity.IsSyncOn && _executionProfileProvider?.IsInteractive == false)
                {
                    BackendDebugLog.Debug(
                        "The synchronization listener is available while the backend is running in background-only mode.",
                        "Presence");
                }
            }
            catch (Exception ex)
            {
                listener.Stop();
                lifetime.Dispose();
                BackendDebugLog.Error($"TCP sync server could not start on 0.0.0.0:{SyncConstants.SyncPort}: {ex.Message}", ex);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }


    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var lifetime = _cts;
            var listener = _listener;
            var acceptLoopTask = _acceptLoopTask;

            _cts = null;
            _listener = null;
            _acceptLoopTask = null;

            lifetime?.Cancel();
            listener?.Stop();

            if (acceptLoopTask is not null)
                await Task.WhenAny(acceptLoopTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));

            lifetime?.Dispose();

            if (listener is not null)
                BackendDebugLog.Info($"TCP sync server stopped on port {SyncConstants.SyncPort}.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }


    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                BackendDebugLog.Error($"TCP sync server accept failed: {ex.Message}", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            if (!TryBeginConnection(client, out var remoteIp))
            {
                BackendDebugLog.Info("TCP sync server rejected an incoming connection because the connection limit was reached.");
                client.Dispose();
                continue;
            }

            BackendDebugLog.Info($"TCP sync server accepted an incoming connection from {remoteIp}.");
            _ = Task.Run(() => HandleClientAsync(client, remoteIp, ct), CancellationToken.None);
        }
    }


    private async Task HandleClientAsync(TcpClient client, string remoteIp, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveTimeout = SyncConstants.SyncTcpIdleTimeoutSeconds * 1000;
                client.SendTimeout = SyncConstants.SyncTcpWriteTimeoutSeconds * 1000;

                await using var ssl = new SslStream(client.GetStream(), false);

                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpHandshakeTimeoutSeconds));

                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                }, handshakeTimeout.Token);

                BackendDebugLog.Info($"TLS authentication completed for incoming connection from {remoteIp}.");

                var context = new PeerConnectionContext
                {
                    RemoteIpAddress = remoteIp,
                    ClientCertificateFingerprint = BuildRemoteCertificateFingerprint(ssl.RemoteCertificate)
                };

                await HandleFramesAsync(ssl, context, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (AuthenticationException ex)
        {
            BackendDebugLog.Error($"TLS authentication failed for incoming connection from {remoteIp}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            BackendDebugLog.Error($"TCP sync connection from {remoteIp} ended with an I/O error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            BackendDebugLog.Error($"TCP sync connection from {remoteIp} failed: {ex.Message}", ex);
        }
        finally
        {
            EndConnection(remoteIp);
        }
    }


    private async Task HandleFramesAsync(Stream stream, PeerConnectionContext context, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct);
            if (frame is null)
                return;

            try
            {
                switch (frame.Type)
                {
                    case SyncTcpMessageType.HelloRequest:
                        if (!await HandleHelloAsync(stream, frame, context, ct))
                            return;
                        break;

                    case SyncTcpMessageType.PushDeltaStart:
                        if (!context.SyncHelloAccepted)
                            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful sync hello is required before sending deltas.");

                        await HandlePushDeltaAsync(stream, context, ct);
                        return;

                    case SyncTcpMessageType.UserSnapshotInventoryRequest:
                        if (!context.SyncHelloAccepted)
                            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful sync hello is required before exchanging snapshot inventory.");

                        await HandleUserSnapshotInventoryAsync(stream, frame, context, ct);
                        return;

                    case SyncTcpMessageType.UserSnapshotRequestBatch:
                        if (!context.SyncHelloAccepted)
                            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful sync hello is required before requesting snapshots.");

                        await HandleUserSnapshotRequestsAsync(stream, frame, context, ct);
                        return;

                    case SyncTcpMessageType.UserControlOperationInventoryRequest:
                        if (!context.SyncHelloAccepted)
                            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful sync hello is required before exchanging control-operation inventory.");

                        await HandleUserControlOperationInventoryAsync(stream, frame, context, ct);
                        return;

                    case SyncTcpMessageType.UserControlOperationRequestBatch:
                        if (!context.SyncHelloAccepted)
                            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful sync hello is required before requesting control operations.");

                        await HandleUserControlOperationRequestsAsync(stream, frame, context, ct);
                        return;

                    case SyncTcpMessageType.GetDeviceEnrollmentInfoRequest:
                        await HandleGetDeviceEnrollmentInfoAsync(stream, frame, ct);
                        return;

                    case SyncTcpMessageType.CompleteDeviceEnrollmentStart:
                        await HandleCompleteDeviceEnrollmentAsync(stream, frame, context, ct);
                        return;

                    default:
                        await WriteErrorAsync(stream, SyncProtocolStatusCode.InvalidArgument, "Unsupported sync TCP frame type.", ct);
                        return;
                }
            }
            catch (SyncProtocolException ex)
            {
                await WriteErrorAsync(stream, ex.StatusCode, ex.Message, ct);
                return;
            }
            catch (InvalidDataException ex)
            {
                await WriteErrorAsync(stream, SyncProtocolStatusCode.InvalidArgument, ex.Message, ct);
                return;
            }
        }
    }


    private async Task<bool> HandleHelloAsync(Stream stream, SyncTcpFrame frame, PeerConnectionContext context, CancellationToken ct)
    {
        var request = frame.Parse(HelloRequest.Parser);
        var reply = await _handler.HelloAsync(request, context, ct);
        if (reply.Ok)
        {
            context.RemoteDatabaseVersion = request.DatabaseVersion;
            context.RemoteProtocolVersion = request.ProtocolVersion;
            context.SyncHelloAccepted = true;
        }

        await WriteFrameAsync(stream, SyncTcpMessageType.HelloReply, reply, ct);
        return reply.Ok;
    }


    private async Task HandlePushDeltaAsync(Stream stream, PeerConnectionContext context, CancellationToken ct)
    {
        var ack = await _handler.PushDeltaAsync(ReadDeltaChunksAsync(stream, ct), context, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.Ack, ack, ct);
    }


    private async IAsyncEnumerable<DeltaChunk> ReadDeltaChunksAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct) ?? throw new EndOfStreamException();
            if (frame.Type == SyncTcpMessageType.PushDeltaEnd)
                yield break;

            if (frame.Type != SyncTcpMessageType.DeltaChunk)
                throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Expected delta chunk frame.");

            yield return frame.Parse(DeltaChunk.Parser);
        }
    }


    private async Task HandleUserSnapshotInventoryAsync(
        Stream stream,
        SyncTcpFrame frame,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        var request = frame.Parse(UserSnapshotInventoryExchangeRequest.Parser);
        var reply = await _handler.ExchangeUserSnapshotInventoryAsync(request, context, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserSnapshotInventoryReply, reply, ct);
    }


    private async Task HandleUserSnapshotRequestsAsync(
        Stream stream,
        SyncTcpFrame frame,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        var request = frame.Parse(UserSnapshotRequestBatch.Parser);
        var deltas = await _handler.RequestUserSnapshotsAsync(request, context, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserSnapshotRelayStart, ct);
        foreach (var delta in deltas)
            await WriteFrameAsync(stream, SyncTcpMessageType.DeltaChunk, DeltaMapping.ToProto(delta), ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserSnapshotRelayEnd, ct);
    }


    private async Task HandleUserControlOperationInventoryAsync(
        Stream stream,
        SyncTcpFrame frame,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        var request = frame.Parse(UserControlOperationInventoryExchangeRequest.Parser);
        var reply = await _handler.ExchangeUserControlOperationInventoryAsync(request, context, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserControlOperationInventoryReply, reply, ct);
    }

    private async Task HandleUserControlOperationRequestsAsync(
        Stream stream,
        SyncTcpFrame frame,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        var request = frame.Parse(UserControlOperationRequestBatch.Parser);
        var deltas = await _handler.RequestUserControlOperationsAsync(request, context, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserControlOperationRelayStart, ct);
        foreach (var delta in deltas)
            await WriteFrameAsync(stream, SyncTcpMessageType.DeltaChunk, DeltaMapping.ToProto(delta), ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.UserControlOperationRelayEnd, ct);
    }

    private async Task HandleGetDeviceEnrollmentInfoAsync(Stream stream, SyncTcpFrame frame, CancellationToken ct)
    {
        var request = frame.Parse(GetDeviceEnrollmentInfoRequest.Parser);
        var reply = await _handler.GetDeviceEnrollmentInfoAsync(request, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.GetDeviceEnrollmentInfoReply, reply, ct);
    }


    private async Task HandleCompleteDeviceEnrollmentAsync(Stream stream, SyncTcpFrame startFrame, PeerConnectionContext context, CancellationToken ct)
    {
        var request = startFrame.Parse(CompleteDeviceEnrollmentStartRequest.Parser);
        var reply = await _handler.CompleteDeviceEnrollmentStreamAsync(ReadEnrollmentChunksAsync(stream, ct), context, request.SourceDatabaseVersion, ct);
        await WriteFrameAsync(stream, SyncTcpMessageType.CompleteDeviceEnrollmentReply, reply, ct);
    }


    private async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> ReadEnrollmentChunksAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var totalBytes = 0L;

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct) ?? throw new EndOfStreamException();
            if (frame.Type == SyncTcpMessageType.CompleteDeviceEnrollmentEnd)
                yield break;

            if (frame.Type != SyncTcpMessageType.CompleteDeviceEnrollmentChunk)
                throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Expected enrollment chunk frame.");

            var chunk = frame.Parse(CompleteDeviceEnrollmentChunk.Parser);
            totalBytes += chunk.SnapshotChunk.Length;
            if (totalBytes > SyncConstants.MaxDeviceEnrollmentSnapshotBytes)
                throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "The profile data is too large to transfer in one enrollment request.");

            yield return chunk;
        }
    }


    private async Task WriteErrorAsync(Stream stream, SyncProtocolStatusCode statusCode, string message, CancellationToken ct)
    {
        await WriteFrameAsync(stream, SyncTcpMessageType.Error, new SyncError
        {
            Code = statusCode.ToString(),
            Message = message
        }, ct);
    }


    private async Task<SyncTcpFrame?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpIdleTimeoutSeconds));
        return await SyncTcpFrameIo.ReadAsync(stream, timeout.Token);
    }


    private async Task WriteFrameAsync(Stream stream, SyncTcpMessageType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpWriteTimeoutSeconds));
        await SyncTcpFrameIo.WriteAsync(stream, type, timeout.Token);
    }


    private async Task WriteFrameAsync(Stream stream, SyncTcpMessageType type, IMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpWriteTimeoutSeconds));
        await SyncTcpFrameIo.WriteAsync(stream, type, message, timeout.Token);
    }


    private bool TryBeginConnection(TcpClient client, out string remoteIp)
    {
        remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

        if (!_connectionSlots.Wait(0))
            return false;

        var count = _connectionsByRemoteIp.AddOrUpdate(remoteIp, 1, (_, current) => current + 1);
        if (count <= SyncConstants.MaxSyncConnectionsPerRemoteIp)
            return true;

        EndConnection(remoteIp);
        return false;
    }


    private void EndConnection(string remoteIp)
    {
        _connectionSlots.Release();

        _connectionsByRemoteIp.AddOrUpdate(
            remoteIp,
            0,
            (_, current) => current <= 1 ? 0 : current - 1);

        if (_connectionsByRemoteIp.TryGetValue(remoteIp, out var count) && count <= 0)
            _connectionsByRemoteIp.TryRemove(remoteIp, out _);
    }


    private string? BuildRemoteCertificateFingerprint(X509Certificate? certificate)
    {
        if (certificate is null)
            return null;

        return _identity.GetFingerprintHex(new X509Certificate2(certificate));
    }
}
