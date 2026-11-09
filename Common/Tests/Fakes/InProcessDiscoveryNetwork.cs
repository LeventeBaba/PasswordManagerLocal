using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using System.Net;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class InProcessDiscoveryNetwork
{
    private readonly ConcurrentDictionary<string, Port> _ports = new();
    private readonly ConcurrentDictionary<string, byte> _blocked = new();
    public void BlockPair(string first, string second)
    {
        _blocked[Key(first, second)] = 0;
        _blocked[Key(second, first)] = 0;
    }
    private static string Key(string source, string destination) => source + "|" + destination;
    private bool CanDeliver(string source, string destination) => !_blocked.ContainsKey(Key(source, destination));
    public ILocalDiscoveryTransport CreatePort(string address)
    {
        var port = new Port(this, address);
        if (!_ports.TryAdd(address, port)) throw new InvalidOperationException("Duplicate discovery address.");
        return port;
    }

    private sealed class Port(InProcessDiscoveryNetwork network, string address) : ILocalDiscoveryTransport
    {
        private Func<LocalDiscoveryDatagram, CancellationToken, Task>? _receiver;
        public Task StartAsync(Func<LocalDiscoveryDatagram, CancellationToken, Task> receiveHandler, CancellationToken ct = default)
        {
            _receiver = receiveHandler;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            _receiver = null;
            return Task.CompletedTask;
        }
        public async Task SendMulticastAsync(byte[] payload, CancellationToken ct = default)
        {
            foreach (var pair in network._ports)
                if (pair.Key != address && network.CanDeliver(address, pair.Key)) await pair.Value.ReceiveAsync(payload, address, ct);
        }
        public Task SendUnicastAsync(byte[] payload, IPEndPoint remoteEndpoint, CancellationToken ct = default) =>
            network.CanDeliver(address, remoteEndpoint.Address.ToString()) && network._ports.TryGetValue(remoteEndpoint.Address.ToString(), out var target)
                ? target.ReceiveAsync(payload, address, ct) : Task.CompletedTask;
        private Task ReceiveAsync(byte[] payload, string sender, CancellationToken ct) =>
            _receiver?.Invoke(new LocalDiscoveryDatagram
            {
                Payload = payload.ToArray(), RemoteEndpoint = new IPEndPoint(IPAddress.Parse(sender), 26689)
            }, ct) ?? Task.CompletedTask;
    }
}
