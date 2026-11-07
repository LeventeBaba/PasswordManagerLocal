using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteSyncRouteRepository(SqliteSession session) : ISyncRouteRepository
{
    // Route eligibility is a narrow relationship projection. Peer trust and membership
    // authorization are evaluated by the calling services.
    private const string EligibleRoutes = """
        FROM "UserDevices" ud WHERE ud."DeviceId" = $device AND ud."IsDeleted" = 0 AND ud."IsSyncOn" = 1
          AND EXISTS (SELECT 1 FROM "LocalUserDevices" lu WHERE lu."UserId" = ud."UserId" AND lu."IsSyncOn" = 1)
        """;

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
