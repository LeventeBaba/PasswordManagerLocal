using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using System.Net;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal sealed class LoopbackSyncListener(TcpSyncServerHostedService server, IPEndPoint endpoint) : IAsyncDisposable
{
    public IPEndPoint Endpoint { get; private set; } = endpoint;

    public static async Task<LoopbackSyncListener> StartAsync(IServiceProvider services, CancellationToken ct)
    {
        var server = new TcpSyncServerHostedService(
            services.GetRequiredService<IDeviceIdentityService>(),
            services.GetRequiredService<SyncPeerProtocolHandler>(),
            services.GetRequiredService<IEnrollmentRuntimeState>());
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), ct);
        return new LoopbackSyncListener(server, server.BoundEndpoint
            ?? throw new AssertFailedException("The production listener did not bind to loopback."));
    }

    public Task StopAsync(CancellationToken ct) => server.StopAsync(ct);

    public async Task RestartAsync(CancellationToken ct)
    {
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), ct);
        Endpoint = server.BoundEndpoint ?? throw new AssertFailedException("The production listener did not restart.");
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
