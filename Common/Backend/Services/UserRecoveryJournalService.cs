using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Temporary installation-local recovery journal; never exported or synchronized.</summary>
public sealed class UserRecoveryJournalService : IUserRecoveryJournalService
{
    private const int MaximumPayloadBytes = 128 * 1024 * 1024;
    private readonly AppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IKeyProtector _protector;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserSyncKeyResolverService _keys;
    private readonly IUserCanonicalHealthService _health;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly ISyncVersionClockService _clock;
    private readonly IUserLifecycleCoordinator _lifecycle;

    public UserRecoveryJournalService(AppDbContext db, IUnitOfWork uow, IKeyProtector protector,
        IDeviceIdentityService identity, IUserSyncKeyResolverService keys, IUserCanonicalHealthService health,
        IUserDataReaderService reader, IUserDataWriterService writer, ISyncVersionClockService clock,
        IUserLifecycleCoordinator lifecycle)
    {
        _db = db; _uow = uow; _protector = protector; _identity = identity;
        _keys = keys; _health = health; _reader = reader; _writer = writer;
        _clock = clock; _lifecycle = lifecycle;
    }

    public async Task<Guid> StageAsync(Guid userId, byte[] snapshot, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A recovery journal must share the history-reset transaction.");
        try
        {
            var decoded = JsonSerializer.Deserialize(snapshot, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot)
                ?? throw new InvalidDataException("Missing recovery snapshot.");
            if (decoded.FormatVersion != 1 || userId == Guid.Empty || decoded.User.UId != userId || decoded.JournalId == Guid.Empty || decoded.EvidenceOnly ||
                decoded.TargetKeyEpoch <= decoded.User.KeyEpoch)
                throw new InvalidDataException("Invalid recovery snapshot identity or namespace.");
            var row = new UserRecoveryJournal
            {
                Id = decoded.JournalId, UserId = userId, CreatedAtUtc = decoded.CreatedAtUtc,
                State = UserRecoveryJournalState.PendingVerification
            };
            await ProtectPayloadAsync(row, snapshot);
            _db.UserRecoveryJournals.Add(row);
            await _uow.SaveChangesAsync(ct);
            await VerifyPersistedPayloadAsync(row.Id, snapshot, ct);
            return row.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecoveryJournalPersistenceException(ex);
        }
    }

    private static byte[] AssociatedData(UserRecoveryJournal row) => Hashing.SHA256Hash(hash =>
    {
        hash.WriteString("PasswordManagerLocal.DeviceRecoveryJournal.v1");
        hash.Write(row.Id); hash.Write(row.UserId); hash.Write((int)row.State);
    });

    private async Task ProtectPayloadAsync(UserRecoveryJournal row, byte[] payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Recovery data exceeds the database journal limit.");
        using var key = EncryptionKey.Create();
        var raw = key.ExportCopy();
        try { row.ProtectedKey = _protector.Protect(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
        row.EncryptedPayload = await AES256.EncryptAsync(payload, key, AssociatedData(row));
        row.PayloadHash = SHA256.HashData(payload);
    }

    private async Task<byte[]> ReadPayloadAsync(UserRecoveryJournal row)
    {
        var raw = _protector.Unprotect(row.ProtectedKey);
        try
        {
            using var key = EncryptionKey.FromRaw(raw);
            var payload = await AES256.DecryptAsync(row.EncryptedPayload, key, AssociatedData(row));
            if (payload.Length > MaximumPayloadBytes || !Hashing.Verify(row.PayloadHash, SHA256.HashData(payload)))
            {
                CryptographicOperations.ZeroMemory(payload);
                throw new InvalidDataException("The recovery journal content commitment is invalid.");
            }
            return payload;
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private async Task VerifyPersistedPayloadAsync(Guid id, byte[] expected, CancellationToken ct)
    {
        // A separate no-tracking query reads the stored row, not the mutable staged object.
        var persisted = await _db.UserRecoveryJournals.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        var restored = await ReadPayloadAsync(persisted);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, restored))
                throw new InvalidDataException("The stored recovery journal could not be verified.");
        }
        finally { CryptographicOperations.ZeroMemory(restored); }
    }

    public async Task<bool> CompleteAsync(Guid journalId, CancellationToken ct = default)
    {
        var userId = await _db.UserRecoveryJournals.AsNoTracking().Where(row => row.Id == journalId)
            .Select(row => (Guid?)row.UserId).SingleOrDefaultAsync(ct);
        if (!userId.HasValue) return false;
        return await _lifecycle.ExecuteAsync(userId.Value, inner => CompleteUnderLifecycleAsync(journalId, inner), ct);
    }

    private async Task<bool> CompleteUnderLifecycleAsync(Guid journalId, CancellationToken ct)
    {
        var row = await _db.UserRecoveryJournals.SingleOrDefaultAsync(item => item.Id == journalId, ct);
        if (row is null) return false;
        // Missing canonical rows are not proof of explicit account deletion. The account
        // deletion transaction removes its journals itself; orphan evidence stays protected.
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.UId == row.UserId, ct);
        if (user is null || await _db.DeletedUserBarriers.AnyAsync(item => item.UserId == row.UserId, ct)) return true;
        if (!_keys.TryResolve(user, out var key, out var confidence) || key is null) return true;
        using (key)
        {
            var health = await _health.VerifyAsync(user, key, confidence, recordFault: false, ct: ct);
            if (!health.FullyVerified) return true;
            var payload = await ReadPayloadAsync(row);
            UserRecoveryJournalSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize(payload, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
            if (snapshot is null || snapshot.User.UId != user.UId || snapshot.JournalId != row.Id) return true;
            if (snapshot.EvidenceOnly)
            {
                if (!await EvidenceHasBeenResolvedAsync(snapshot, user, ct)) return true;
                await DeleteJournalAsync(row, ct);
                return false;
            }
            if (snapshot.TargetKeyEpoch <= 0 || user.KeyEpoch < snapshot.TargetKeyEpoch ||
                !Hashing.Verify(snapshot.User.EncryptedUserPasswordsDataPayload, user.EncryptedUserPasswordsDataPayload) ||
                !Hashing.Verify(snapshot.User.EncryptedGeneralUserDataPayload, user.EncryptedGeneralUserDataPayload)) return true;
            var state = await _db.UserControlStates.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.UId, ct);
            var local = await _db.LocalUserDevices.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.UId, ct);
            var own = await _db.UserMembershipAuthorizations.AsNoTracking().Where(item => item.UserId == user.UId &&
                item.IsActive && item.DeviceId == _identity.LocalDeviceId && item.OriginInstanceId == _identity.OriginInstanceId).ToListAsync(ct);
            if (state is null || state.HasConflict || state.AppliedKeyEpoch != user.KeyEpoch || state.AppliedMembershipEpoch != user.MembershipEpoch ||
                local is null || !local.IsIntegrityValid() || local.LocalDeviceIdentityId != _identity.LocalDeviceId || own.Count != 1 ||
                !Hashing.Verify(own[0].SignPublicKey, _identity.SignPublicKey) ||
                await _db.UserSyncFaults.AnyAsync(item => item.UserId == user.UId &&
                    item.Status != UserSyncHealthStatus.Recovered && item.Status != UserSyncHealthStatus.Superseded &&
                    (item.BlocksPublishing || item.BlocksLogin || item.BlocksMerge || item.BlocksLifecycle), ct)) return true;

            // Resume only the already-authorized presentation cleanup. Never restore old
            // memberships, replay operations, or roll back across the new causal namespace.
            if (snapshot.Confirmed) await FinishDeviceMetadataAsync(user, key, ct);
            user = await _db.Users.AsNoTracking().SingleAsync(item => item.UId == row.UserId, ct);
            if (!(await _health.VerifyAsync(user, key, confidence, recordFault: false, ct: ct)).FullyVerified) return true;
            if (!Hashing.Verify(snapshot.User.EncryptedUserPasswordsDataPayload, user.EncryptedUserPasswordsDataPayload) ||
                !Hashing.Verify(snapshot.User.EncryptedGeneralUserDataPayload, user.EncryptedGeneralUserDataPayload)) return true;

            // The temporary full profile copy is no longer needed. Retain only unresolved
            // received evidence and the history needed to interpret it, without another local vault copy.
            snapshot.Snapshots = snapshot.Snapshots.Where(item => item.Status is UserSyncSnapshotStatus.Pending or
                UserSyncSnapshotStatus.RecoveryCandidate or UserSyncSnapshotStatus.IsolatedFork).ToList();
            snapshot.Knowledge = snapshot.Knowledge.Where(item => item.HighestStoredRevision > item.HighestMergedRevision).ToList();
            var pendingOperations = snapshot.Operations.Any(item => item.Status == UserControlOperationStatus.StoredPending);
            if (snapshot.Snapshots.Count == 0 && snapshot.Knowledge.Count == 0 && !pendingOperations)
            {
                await DeleteJournalAsync(row, ct);
                return false;
            }
            snapshot.EvidenceOnly = true;
            snapshot.User = new DeviceEnrollmentUserSnapshot { UId = user.UId, PasswordSalt = snapshot.User.PasswordSalt };
            // Keep an already-existing OS-protected remembered key only as historical
            // evidence for old encrypted snapshots; never create a new remembered key
            // or expose it through the ordinary session/key resolver.
            snapshot.DeviceLinks.Clear(); snapshot.LocalLinkIntegrityHash = [];
            snapshot.HasLocalLink = false; snapshot.LocalDeviceIdentityId = Guid.Empty; snapshot.LocalSyncOn = false;
            snapshot.ControlState = null; snapshot.SyncState = null; snapshot.Faults.Clear();
            await ReplaceWithEvidenceAsync(row, snapshot, ct);
            return true;
        }
    }

    private async Task FinishDeviceMetadataAsync(User user, EncryptionKey key, CancellationToken ct)
    {
        var active = (await _db.UserMembershipAuthorizations.AsNoTracking().Where(item => item.UserId == user.UId && item.IsActive)
            .Select(item => item.DeviceId).ToListAsync(ct)).ToHashSet();
        active.Add(_identity.LocalDeviceId);
        using var bundle = await _reader.GetAndVerifyUserDataBundleAsync(user, key, ct);
        var stale = bundle.UserDevicesData.Devices.Where(item => !active.Contains(item.Id)).ToArray();
        if (stale.Length == 0) return;
        _clock.Observe(stale.Select(item => item.Version).Concat(bundle.UserDevicesData.DeletedDevices.Select(item => item.Version)));
        foreach (var item in stale)
        {
            TombstoneCleanupUtil.AddOrUpdateDeletedUserDevice(bundle.UserDevicesData, item.Id, DateTimeOffset.UtcNow, _clock.Next());
            bundle.UserDevicesData.Devices.Remove(item); item.Dispose();
        }
        // Use the tracked canonical instance so an earlier reset in this scope cannot
        // create a duplicate tracked user; the verified bundle's generation is checked by the writer.
        var tracked = await _db.Users.SingleAsync(item => item.UId == user.UId, ct);
        await _writer.UpdateUserDataBundleAsync(bundle, tracked, key, UserDataBlobKind.Devices, true, ct);
    }

    private async Task ReplaceWithEvidenceAsync(UserRecoveryJournal row, UserRecoveryJournalSnapshot snapshot, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            row.State = UserRecoveryJournalState.UnresolvedEvidence;
            await ProtectPayloadAsync(row, bytes);
            _db.UserRecoveryJournals.Update(row);
            await _uow.SaveChangesAsync(ct);
            await VerifyPersistedPayloadAsync(row.Id, bytes, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None); _uow.ClearTrackedChanges(); throw;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task DeleteJournalAsync(UserRecoveryJournal row, CancellationToken ct)
    {
        _db.UserRecoveryJournals.Remove(row);
        await _uow.SaveChangesAsync(ct);
        BackendDebugLog.Info($"Removed verified temporary recovery data. UserId={row.UserId}, JournalId={row.Id}.", "SelfDiagnostics");
    }

    private async Task<bool> EvidenceHasBeenResolvedAsync(UserRecoveryJournalSnapshot snapshot, User user, CancellationToken ct)
    {
        // A superseded/quarantined status, a healthy reset, or a new pairing is NOT evidence of a merge.
        foreach (var retained in snapshot.Snapshots)
        {
            if (retained.Status == UserSyncSnapshotStatus.IsolatedFork) return false;
            UserSnapshotEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(retained.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
                if (envelope is null || envelope.UserId != user.UId || envelope.OriginDeviceId != retained.OriginDeviceId ||
                    envelope.OriginInstanceId != retained.OriginInstanceId || envelope.UserKeyEpoch != retained.UserKeyEpoch ||
                    envelope.OriginRevision != retained.OriginRevision || !Hashing.Verify(envelope.SnapshotHash, retained.SnapshotHash)) return false;
                var authors = snapshot.Authorizations.Where(item => item.DeviceId == envelope.OriginDeviceId &&
                    item.OriginInstanceId == envelope.OriginInstanceId && Hashing.Verify(item.SignPublicKeyHash, Hashing.SHA256Hash(item.SignPublicKey))).ToArray();
                if (authors.Length != 1) return false;
                UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, authors[0].SignPublicKey);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException) { return false; }
            var known = await _db.UserRevisionKnowledge.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.UId &&
                item.OriginDeviceId == retained.OriginDeviceId && item.OriginInstanceId == retained.OriginInstanceId && item.UserKeyEpoch == retained.UserKeyEpoch, ct);
            if (known is null || known.HighestMergedRevision < retained.OriginRevision ||
                (known.HighestMergedRevision == retained.OriginRevision && known.HighestStoredRevision == retained.OriginRevision &&
                    !Hashing.Verify(known.HighestStoredSnapshotHash, retained.SnapshotHash))) return false;
        }
        foreach (var retained in snapshot.Knowledge)
        {
            var known = await _db.UserRevisionKnowledge.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.UId &&
                item.OriginDeviceId == retained.OriginDeviceId && item.OriginInstanceId == retained.OriginInstanceId && item.UserKeyEpoch == retained.UserKeyEpoch, ct);
            if (known is null || known.HighestMergedRevision < retained.HighestStoredRevision) return false;
        }
        foreach (var pending in snapshot.Operations.Where(item => item.Status == UserControlOperationStatus.StoredPending))
        {
            var current = await _db.UserControlOperations.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.UId && item.OperationId == pending.OperationId, ct);
            if (current is null || current.Status != UserControlOperationStatus.Rejected ||
                !Hashing.Verify(current.OperationHash, pending.OperationHash)) return false;
        }
        return true;
    }

    public async Task MaintainAsync(CancellationToken ct = default)
    {
        var ids = await _db.UserRecoveryJournals.AsNoTracking().Select(row => row.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await CompleteAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _uow.ClearTrackedChanges();
                BackendDebugLog.Warning($"Recovery journal remains protected for a later verification attempt. JournalId={id}.", ex, "SelfDiagnostics");
            }
        }
    }
}
