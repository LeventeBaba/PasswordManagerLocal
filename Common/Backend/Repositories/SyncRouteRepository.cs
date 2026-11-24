using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class SyncRouteRepository(SqliteSession session) : ISyncRouteRepository
{
    // Route eligibility is a narrow relationship projection. Peer trust and membership
    // authorization are evaluated by the calling services.
    private const string ActiveRoutes = """
        FROM "UserDevices" ud WHERE ud."DeviceId" = $device AND ud."IsDeleted" = 0 AND ud."IsSyncOn" = 1
          AND EXISTS (SELECT 1 FROM "LocalUserDevices" lu WHERE lu."UserId" = ud."UserId" AND lu."IsSyncOn" = 1)
        """;
    private const string EligibleRoutes = ActiveRoutes + """

          AND NOT EXISTS (SELECT 1 FROM "CriticalControlIngress" ci WHERE ci."UserId" = ud."UserId")
        """;

    public async Task<IReadOnlyList<Guid>> ListAllControlEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default) =>
        remoteDeviceId == Guid.Empty ? [] : await session.ReadAsync(
            $"SELECT DISTINCT ud.\"UserId\" {ActiveRoutes} ORDER BY ud.\"UserId\";", row => row.GetGuid(0), ct, ("$device", remoteDeviceId));

    public Task<bool> IsControlEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
        userId == Guid.Empty || remoteDeviceId == Guid.Empty ? Task.FromResult(false) : session.ExistsAsync(
            $"""SELECT 1 {ActiveRoutes} AND ud."UserId" = $user""", ct, ("$device", remoteDeviceId), ("$user", userId));

    public Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
        userId == Guid.Empty || remoteDeviceId == Guid.Empty
            ? Task.FromResult(false)
            : session.ExistsAsync($"""SELECT 1 {EligibleRoutes} AND ud."UserId" = $user""",
                ct, ("$device", remoteDeviceId), ("$user", userId));

    public async Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default) =>
        (await ListEligibleUserIdsAsync(userIds, remoteDeviceId, ct)).Count != 0;

    public async Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default)
    {
        if (remoteDeviceId == Guid.Empty)
            return [];

        return await session.ReadByIdsAsync(userIds.Where(id => id != Guid.Empty).ToArray(),
            """SELECT DISTINCT ud."UserId" """ + EligibleRoutes + """ AND ud."UserId" IN ({ids});""",
            row => row.GetGuid(0), ct, ("$device", remoteDeviceId));
    }

    public async Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default)
    {
        if (remoteDeviceId == Guid.Empty)
            return [];

        return await session.ReadAsync($"""
            SELECT DISTINCT ud."UserId" {EligibleRoutes} ORDER BY ud."UserId";
            """, row => row.GetGuid(0), ct, ("$device", remoteDeviceId));
    }

    public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        deviceId == Guid.Empty
            ? Task.FromResult(false)
            : session.ExistsAsync($"SELECT 1 {EligibleRoutes}", ct, ("$device", deviceId));
}
