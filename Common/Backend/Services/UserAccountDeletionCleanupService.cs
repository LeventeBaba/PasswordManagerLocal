using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Removes all mutable account state while deliberately retaining deletion/control evidence and
/// historical membership authorization needed to verify and relay the deletion operation.
/// </summary>
public sealed class UserAccountDeletionCleanupService : IUserAccountDeletionCleanupService
{
    private readonly AppDbContext _db;

    public UserAccountDeletionCleanupService(AppDbContext db) => _db = db;

    public async Task DeleteCanonicalAndPendingStateAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("The deleted user identity is invalid.", nameof(userId));

        var userDeviceRows = await _db.UserDevices
            .Where(link => link.UserId == userId)
            .Select(link => new { link.ModelId, link.DeviceId })
            .ToListAsync(ct);
        var userDeviceModelIds = userDeviceRows.Select(link => link.ModelId).ToList();
        var linkedDeviceIds = userDeviceRows.Select(link => link.DeviceId).Distinct().ToList();
        var accountOnlyDeviceIds = await _db.Devices
            .Where(device => linkedDeviceIds.Contains(device.Id) &&
                             !_db.UserDevices.Any(link =>
                                 link.DeviceId == device.Id &&
                                 link.UserId != userId &&
                                 !link.IsDeleted))
            .Select(device => device.Id)
            .ToListAsync(ct);

        var accountOnlyGroupIds = await _db.Groups
            .Where(group => group.Users.Any(member => member.UId == userId) &&
                            !group.Users.Any(member => member.UId != userId))
            .Select(group => group.Id)
            .ToListAsync(ct);

        var ordinarySyncItems = await _db.SyncItems
            .Where(item =>
                (item.ModelType == SyncModelType.User && item.ModelId == userId) ||
                (item.ModelType == SyncModelType.UserDevice && userDeviceModelIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Group && accountOnlyGroupIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Device && accountOnlyDeviceIds.Contains(item.ModelId)))
            .ToListAsync(ct);
        if (ordinarySyncItems.Count != 0)
            _db.SyncItems.RemoveRange(ordinarySyncItems);

        var snapshots = await _db.UserSyncSnapshots.Where(item => item.UserId == userId).ToListAsync(ct);
        if (snapshots.Count != 0)
            _db.UserSyncSnapshots.RemoveRange(snapshots);

        var revisionKnowledge = await _db.UserRevisionKnowledge.Where(item => item.UserId == userId).ToListAsync(ct);
        if (revisionKnowledge.Count != 0)
            _db.UserRevisionKnowledge.RemoveRange(revisionKnowledge);

        var syncState = await _db.UserSyncStates.FirstOrDefaultAsync(item => item.UserId == userId, ct);
        if (syncState is not null)
            _db.UserSyncStates.Remove(syncState);

        var checkpoint = await _db.UserCanonicalCheckpoints.FirstOrDefaultAsync(item => item.UserId == userId, ct);
        if (checkpoint is not null)
            _db.UserCanonicalCheckpoints.Remove(checkpoint);

        var healthFaults = await _db.UserSyncFaults.Where(item => item.UserId == userId).ToListAsync(ct);
        if (healthFaults.Count != 0)
            _db.UserSyncFaults.RemoveRange(healthFaults);

        var enrollmentCommits = await _db.DeviceEnrollmentCommits.Where(item => item.UserId == userId).ToListAsync(ct);
        if (enrollmentCommits.Count != 0)
            _db.DeviceEnrollmentCommits.RemoveRange(enrollmentCommits);

        var obsoleteTombstones = await _db.SyncTombstones
            .Where(item =>
                (item.ModelId == userId && item.ModelType == SyncModelType.User) ||
                (item.ModelType == SyncModelType.Group && accountOnlyGroupIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Device && accountOnlyDeviceIds.Contains(item.ModelId)))
            .ToListAsync(ct);
        if (obsoleteTombstones.Count != 0)
            _db.SyncTombstones.RemoveRange(obsoleteTombstones);

        if (accountOnlyGroupIds.Count != 0)
        {
            var accountOnlyGroups = await _db.Groups
                .Where(group => accountOnlyGroupIds.Contains(group.Id))
                .ToListAsync(ct);
            foreach (var group in accountOnlyGroups)
                CryptographicOperations.ZeroMemory(group.EncryptedPayload);
            _db.Groups.RemoveRange(accountOnlyGroups);
        }

        var user = await _db.Users.FirstOrDefaultAsync(item => item.UId == userId, ct);
        if (user is null)
            return;

        // The encrypted payload columns are optimistic-concurrency tokens. Preserve independent
        // snapshots of their database values before zeroing the tracked arrays; otherwise mutating
        // a byte[] in place can also mutate the value EF uses in the DELETE predicate and produce a
        // false DbUpdateConcurrencyException even though no concurrent writer touched the account.
        var userEntry = _db.Entry(user);
        userEntry.Property(item => item.EncryptedPayload).OriginalValue = userEntry.Property(item => item.EncryptedPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedGeneralUserDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedGeneralUserDataPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedUserPasswordsDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedUserPasswordsDataPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedUserDevicesDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedUserDevicesDataPayload).OriginalValue.ToArray();

        _db.Users.Remove(user);
        if (user.SavedKey is not null)
            CryptographicOperations.ZeroMemory(user.SavedKey);
        user.SavedKey = null;
        user.ClearEncryptedPayloads();
    }
}
