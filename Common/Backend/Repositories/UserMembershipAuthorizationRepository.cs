using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserMembershipAuthorizationRepository : IUserMembershipAuthorizationRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<UserMembershipAuthorization> _rows;

    public UserMembershipAuthorizationRepository(AppDbContext context)
    {
        _context = context;
        _rows = context.UserMembershipAuthorizations;
    }

    public Task<UserMembershipAuthorization?> GetByIdAsync(Guid authorizationId, CancellationToken ct = default) =>
        _rows.FirstOrDefaultAsync(row => row.AuthorizationId == authorizationId, ct);

    public Task<UserMembershipAuthorization?> GetForSignedEpochAsync(Guid userId, Guid deviceId, Guid originInstanceId, long membershipEpoch, CancellationToken ct = default) =>
        _rows.FirstOrDefaultAsync(row =>
            row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId &&
            row.StartedMembershipEpoch <= membershipEpoch &&
            (row.EndedMembershipEpoch == null || membershipEpoch < row.EndedMembershipEpoch), ct);

    public Task<UserMembershipAuthorization?> GetActiveAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default) =>
        _rows.FirstOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId && row.IsActive, ct);

    public async Task<IReadOnlyList<UserMembershipAuthorization>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _rows.Where(row => row.UserId == userId)
            .OrderBy(row => row.StartedMembershipEpoch).ThenBy(row => row.DeviceId).ThenBy(row => row.OriginInstanceId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserMembershipAuthorization>> ListActiveForDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        await _rows.Where(row => row.UserId == userId && row.DeviceId == deviceId && row.IsActive)
            .OrderBy(row => row.OriginInstanceId).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListUserIdsForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await _context.Devices.AsNoTracking().FirstOrDefaultAsync(row => row.Id == deviceId, ct);
        if (device is null)
            return [];

        var rows = await _rows.AsNoTracking()
            .Where(row => row.DeviceId == deviceId)
            .Select(row => new { row.UserId, row.SignPublicKeyHash, row.TlsCertFingerprint })
            .ToListAsync(ct);
        return rows
            .Where(row => MatchesCurrentDeviceIdentity(row.SignPublicKeyHash, row.TlsCertFingerprint, device))
            .Select(row => row.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListDeviceIdsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        var authorizations = await _rows.AsNoTracking()
            .Where(row => userIds.Contains(row.UserId))
            .Select(row => new { row.DeviceId, row.SignPublicKeyHash, row.TlsCertFingerprint })
            .ToListAsync(ct);
        var candidateIds = authorizations.Select(row => row.DeviceId).Distinct().ToList();
        var devices = await _context.Devices.AsNoTracking()
            .Where(device => candidateIds.Contains(device.Id))
            .ToListAsync(ct);
        return devices
            .Where(device => authorizations.Any(row =>
                row.DeviceId == device.Id &&
                MatchesCurrentDeviceIdentity(row.SignPublicKeyHash, row.TlsCertFingerprint, device)))
            .Select(device => device.Id)
            .Distinct()
            .OrderBy(deviceId => deviceId)
            .ToList();
    }

    public async Task<bool> HasHistoricalAuthorizationAsync(Guid userId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await _context.Devices.AsNoTracking().FirstOrDefaultAsync(row => row.Id == deviceId, ct);
        if (device is null)
            return false;
        var rows = await _rows.AsNoTracking()
            .Where(row => row.UserId == userId && row.DeviceId == deviceId)
            .Select(row => new { row.SignPublicKeyHash, row.TlsCertFingerprint })
            .ToListAsync(ct);
        return rows.Any(row => MatchesCurrentDeviceIdentity(row.SignPublicKeyHash, row.TlsCertFingerprint, device));
    }

    private bool MatchesCurrentDeviceIdentity(byte[] signPublicKeyHash, string tlsCertFingerprint, Device device) =>
        Hashing.Verify(signPublicKeyHash, device.SignPublicKeyHash) &&
        string.Equals(
            SyncIdentityUtil.NormalizeFingerprint(tlsCertFingerprint),
            SyncIdentityUtil.NormalizeFingerprint(device.TlsCertFingerprint),
            StringComparison.OrdinalIgnoreCase);

    public Task AddAsync(UserMembershipAuthorization authorization, CancellationToken ct = default) =>
        _rows.AddAsync(authorization, ct).AsTask();

    public void Update(UserMembershipAuthorization authorization)
    {
        var entry = _context.Entry(authorization);
        if (entry.State == EntityState.Added)
            return;

        if (entry.State == EntityState.Detached)
            _rows.Attach(authorization);

        entry.State = EntityState.Modified;
    }
}
