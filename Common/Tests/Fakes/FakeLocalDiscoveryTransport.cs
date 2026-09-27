using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using System.Net;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeLocalDiscoveryTransport : ILocalDiscoveryTransport
{
    private readonly IList<string>? _calls;
    private Func<LocalDiscoveryDatagram, CancellationToken, Task>? _receiveHandler;

    public FakeLocalDiscoveryTransport(IList<string>? calls = null)
    {
        _calls = calls;
    }

    public List<byte[]> MulticastPayloads { get; } = [];
    public List<(byte[] Payload, IPEndPoint RemoteEndpoint)> UnicastPayloads { get; } = [];
    public bool IsStarted { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public Exception? StartFailure { get; set; }

    public Task StartAsync(Func<LocalDiscoveryDatagram, CancellationToken, Task> receiveHandler, CancellationToken ct = default)
    {
        StartCalls++;
        _calls?.Add("transport:start");
        if (StartFailure is not null)
            return Task.FromException(StartFailure);

        _receiveHandler = receiveHandler;
        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        StopCalls++;
        _calls?.Add("transport:stop");
        IsStarted = false;
        return Task.CompletedTask;
    }

    public Task SendMulticastAsync(byte[] payload, CancellationToken ct = default)
    {
        MulticastPayloads.Add(payload.ToArray());
        return Task.CompletedTask;
    }

    public Task SendUnicastAsync(byte[] payload, IPEndPoint remoteEndpoint, CancellationToken ct = default)
    {
        UnicastPayloads.Add((payload.ToArray(), remoteEndpoint));
        return Task.CompletedTask;
    }

    public Task InjectAsync(byte[] payload, string sourceAddress = "192.168.1.50", int sourcePort = 26689, CancellationToken ct = default)
    {
        if (_receiveHandler is null)
            throw new InvalidOperationException("The fake discovery transport is not started.");

        return _receiveHandler(new LocalDiscoveryDatagram
        {
            Payload = payload.ToArray(),
            RemoteEndpoint = new IPEndPoint(IPAddress.Parse(sourceAddress), sourcePort)
        }, ct);
    }
}
