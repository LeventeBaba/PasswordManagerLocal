using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using System.Net;
using System.Net.Sockets;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;

namespace PasswordManagerLocal.Common.Backend.Services.Discovery;

internal sealed class UdpLocalDiscoveryTransport : ILocalDiscoveryTransport, ISyncRuntimeHealth, IDisposable
{
    private readonly ILocalNetworkAddressService _networkAddresses;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _socket;
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _receiveLoopTask;
    public bool RequiresProcessRestart { get; private set; }

    public bool IsHealthy
    {
        get
        {
            var task = _receiveLoopTask;
            _ = task?.Exception;
            return _socket is not null && task is { IsCompleted: false };
        }
    }

    public UdpLocalDiscoveryTransport(ILocalNetworkAddressService networkAddresses)
    {
        _networkAddresses = networkAddresses;
    }


    public async Task StartAsync(Func<LocalDiscoveryDatagram, CancellationToken, Task> receiveHandler, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receiveHandler);

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (RequiresProcessRestart) throw new InvalidOperationException("The UDP transport requires a complete process restart.");
            if (_socket is not null)
                return;

            var socket = CreateSocket();
            var cancellation = new CancellationTokenSource();

            _socket = socket;
            _lifetimeCancellation = cancellation;
            _receiveLoopTask = PasswordManagerLocal.Common.Backend.Utils.IndependentBackgroundWork.Run(() => ReceiveLoopAsync(socket, receiveHandler, cancellation.Token), CancellationToken.None);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }


    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            var receiveLoopTask = _receiveLoopTask;
            var cancellation = _lifetimeCancellation;
            cancellation?.Cancel();
            _socket?.Dispose();
            if (receiveLoopTask is not null)
            {
                try { await receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(2), ct); }
                catch when (receiveLoopTask.IsCompleted) { }
            }
            await _sendLock.WaitAsync(ct);
            _sendLock.Release();
            _receiveLoopTask = null;
            _lifetimeCancellation = null;
            _socket = null;
            cancellation?.Dispose();
        }
        catch { RequiresProcessRestart = true; throw; }
        finally { _lifecycleLock.Release(); }
    }


    public Task SendMulticastAsync(byte[] payload, CancellationToken ct = default) =>
        SendAsync(payload, multicast: true, remoteEndpoint: null, ct);


    public Task SendUnicastAsync(byte[] payload, IPEndPoint remoteEndpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        return SendAsync(payload, multicast: false, remoteEndpoint, ct);
    }


    public void Dispose()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        _lifecycleLock.Dispose();
        _sendLock.Dispose();
    }


    private Socket CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false
        };

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
            socket.Bind(new IPEndPoint(IPAddress.Any, LocalDiscoveryPort));

            var multicastAddress = IPAddress.Parse(LocalDiscoveryMulticastAddress);
            var joinedMulticastGroup = false;

            foreach (var interfaceAddress in _networkAddresses.GetMulticastInterfaceAddresses())
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(multicastAddress, interfaceAddress));
                    joinedMulticastGroup = true;
                }
                catch
                {
                }
            }

            if (!joinedMulticastGroup)
            {
                // Starting synchronization while the device is completely offline must not
                // prevent the application from starting. There may be no default multicast
                // interface yet, particularly on Android or during a Windows adapter change.
                // The network refresh service will recreate this socket and join the correct
                // interfaces once an address becomes available.
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(multicastAddress));
                }
                catch
                {
                }
            }

        }
        catch { socket.Dispose(); throw; }

        return socket;
    }


    private async Task SendAsync(byte[] payload, bool multicast, IPEndPoint? remoteEndpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length == 0 || payload.Length > LocalDiscoveryMaxPacketBytes)
            throw new InvalidDataException("The local discovery packet has an invalid size.");

        await _sendLock.WaitAsync(ct);
        try
        {
            var socket = _socket ?? throw new InvalidOperationException("The local discovery transport is not running.");

            if (!multicast)
            {
                await socket.SendToAsync(payload, SocketFlags.None, remoteEndpoint!, ct);
                return;
            }

            var destination = new IPEndPoint(IPAddress.Parse(LocalDiscoveryMulticastAddress), LocalDiscoveryPort);
            var interfaceAddresses = _networkAddresses.GetMulticastInterfaceAddresses();

            if (interfaceAddresses.Count == 0)
            {
                await socket.SendToAsync(payload, SocketFlags.None, destination, ct);
                return;
            }

            Exception? lastError = null;
            var sent = false;

            foreach (var interfaceAddress in interfaceAddresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
                    await socket.SendToAsync(payload, SocketFlags.None, destination, ct);
                    sent = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (!sent && lastError is not null)
                throw lastError;
        }
        finally
        {
            _sendLock.Release();
        }
    }


    private async Task ReceiveLoopAsync(
        Socket socket,
        Func<LocalDiscoveryDatagram, CancellationToken, Task> receiveHandler,
        CancellationToken ct)
    {
        var buffer = new byte[LocalDiscoveryMaxPacketBytes + 1];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndpoint, ct);

                if (result.ReceivedBytes <= 0 || result.ReceivedBytes > LocalDiscoveryMaxPacketBytes)
                    continue;

                if (result.RemoteEndPoint is not IPEndPoint ipEndpoint || ipEndpoint.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var payload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                try
                {
                    await receiveHandler(new LocalDiscoveryDatagram { Payload = payload, RemoteEndpoint = ipEndpoint }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { /* Invalid datagrams cannot terminate the authenticated responder. */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch { throw; } // Socket failure is supervised; never spin on a dead receive loop.

        }
    }
}
