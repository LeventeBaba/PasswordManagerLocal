using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class SyncDeviceIdentityWarmupHostedService : ISyncControlledHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDeviceIdentityService _identity;

    public SyncDeviceIdentityWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDeviceIdentityService identity)
    {
        _scopeFactory = scopeFactory;
        _syncDeviceIdentities = syncDeviceIdentities;
        _identity = identity;
    }




    public int StartOrder => 10;




    public async Task StartAsync(CancellationToken ct = default)
    {
        _syncDeviceIdentities.Clear();

        if (!_identity.IsSyncOn)
            return;

        using var scope = _scopeFactory.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var pendingDevices = await devices.ListDevicesNeedingSyncAsync(ct);
        var operations = scope.ServiceProvider.GetService<IUserControlOperationRepository>();
        var membership = scope.ServiceProvider.GetService<IUserMembershipAuthorizationRepository>();

        IReadOnlyList<Device> deletionRelayDevices = [];
        if (operations is not null && membership is not null)
        {
            var deletedUserIds = await operations.ListAppliedAccountDeletionUserIdsAsync(ct);
            var historicalDeviceIds = await membership.ListDeviceIdsForUsersAsync(deletedUserIds, ct);
            deletionRelayDevices = await devices.ListByIdsAsync(historicalDeviceIds, ct);
        }
        var relayCandidates = pendingDevices
            .Concat(deletionRelayDevices)
            .GroupBy(device => device.Id)
            .Select(group => group.First())
            .ToList();
        _syncDeviceIdentities.TryAdd(relayCandidates);
    }


    public Task StopAsync(CancellationToken ct = default)
    {
        _syncDeviceIdentities.Clear();
        return Task.CompletedTask;
    }
}
