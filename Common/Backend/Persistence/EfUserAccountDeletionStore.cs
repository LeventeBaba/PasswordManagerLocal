using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class EfUserAccountDeletionStore(AppDbContext db) : IUserAccountDeletionStore
{
    public async Task DeleteDependentStateAsync(Guid userId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Account cleanup requires the authoritative deletion transaction.");

        var userDeviceRows = await db.UserDevices
            .Where(link => link.UserId == userId)
            .Select(link => new { link.ModelId, link.DeviceId })
            .ToListAsync(ct);
        var userDeviceModelIds = userDeviceRows.Select(link => link.ModelId).ToList();
        var linkedDeviceIds = userDeviceRows.Select(link => link.DeviceId).Distinct().ToList();
        var accountOnlyDeviceIds = await db.Devices
            .Where(device => linkedDeviceIds.Contains(device.Id) &&
                             !db.UserDevices.Any(link =>
                                 link.DeviceId == device.Id &&
                                 link.UserId != userId &&
                                 !link.IsDeleted))
            .Select(device => device.Id)
            .ToListAsync(ct);

        var accountOnlyGroupIds = await db.Groups
            .Where(group => group.Users.Any(member => member.UId == userId) &&
                            !group.Users.Any(member => member.UId != userId))
            .Select(group => group.Id)
            .ToListAsync(ct);

        var ordinarySyncItems = await db.SyncItems
            .Where(item =>
                (item.ModelType == SyncModelType.User && item.ModelId == userId) ||
                (item.ModelType == SyncModelType.UserDevice && userDeviceModelIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Group && accountOnlyGroupIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Device && accountOnlyDeviceIds.Contains(item.ModelId)))
            .ToListAsync(ct);
        if (ordinarySyncItems.Count != 0)
            db.SyncItems.RemoveRange(ordinarySyncItems);

        var recoveryJournals = await db.UserRecoveryJournals.Where(item => item.UserId == userId).ToListAsync(ct);
        if (recoveryJournals.Count != 0)
            db.UserRecoveryJournals.RemoveRange(recoveryJournals);

        var snapshots = await db.UserSyncSnapshots.Where(item => item.UserId == userId).ToListAsync(ct);
        if (snapshots.Count != 0)
            db.UserSyncSnapshots.RemoveRange(snapshots);

        var revisionKnowledge = await db.UserRevisionKnowledge.Where(item => item.UserId == userId).ToListAsync(ct);
        if (revisionKnowledge.Count != 0)
            db.UserRevisionKnowledge.RemoveRange(revisionKnowledge);

        var syncState = await db.UserSyncStates.FirstOrDefaultAsync(item => item.UserId == userId, ct);
        if (syncState is not null)
            db.UserSyncStates.Remove(syncState);

        var checkpoint = await db.UserCanonicalCheckpoints.FirstOrDefaultAsync(item => item.UserId == userId, ct);
        if (checkpoint is not null)
            db.UserCanonicalCheckpoints.Remove(checkpoint);

        var healthFaults = await db.UserSyncFaults.Where(item => item.UserId == userId).ToListAsync(ct);
        if (healthFaults.Count != 0)
            db.UserSyncFaults.RemoveRange(healthFaults);

        var enrollmentCommits = await db.DeviceEnrollmentCommits.Where(item => item.UserId == userId).ToListAsync(ct);
        if (enrollmentCommits.Count != 0)
            db.DeviceEnrollmentCommits.RemoveRange(enrollmentCommits);

        var obsoleteTombstones = await db.SyncTombstones
            .Where(item =>
                (item.ModelId == userId && item.ModelType == SyncModelType.User) ||
                (item.ModelType == SyncModelType.Group && accountOnlyGroupIds.Contains(item.ModelId)) ||
                (item.ModelType == SyncModelType.Device && accountOnlyDeviceIds.Contains(item.ModelId)))
            .ToListAsync(ct);
        if (obsoleteTombstones.Count != 0)
            db.SyncTombstones.RemoveRange(obsoleteTombstones);

        if (accountOnlyGroupIds.Count != 0)
        {
            var accountOnlyGroups = await db.Groups
                .Where(group => accountOnlyGroupIds.Contains(group.Id))
                .ToListAsync(ct);
            foreach (var group in accountOnlyGroups)
                CryptographicOperations.ZeroMemory(group.EncryptedPayload);
            db.Groups.RemoveRange(accountOnlyGroups);
        }
    }

    public void StageUserDelete(User user)
    {
        // The encrypted payload columns are optimistic-concurrency tokens. Preserve independent
        // snapshots of their database values before zeroing the tracked arrays; otherwise mutating
        // a byte[] in place can also mutate the value EF uses in the DELETE predicate and produce a
        // false DbUpdateConcurrencyException even though no concurrent writer touched the account.
        var userEntry = db.Entry(user);
        userEntry.Property(item => item.EncryptedPayload).OriginalValue = userEntry.Property(item => item.EncryptedPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedGeneralUserDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedGeneralUserDataPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedUserPasswordsDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedUserPasswordsDataPayload).OriginalValue.ToArray();
        userEntry.Property(item => item.EncryptedUserDevicesDataPayload).OriginalValue = userEntry.Property(item => item.EncryptedUserDevicesDataPayload).OriginalValue.ToArray();

        db.Users.Remove(user);
    }
}
