using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;
namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Reconstructs an unhealthy local canonical user row only from freshly authenticated,
/// historically authorized and fully decrypted logical evidence. Recovery is committed through
/// the normal checkpoint, local-snapshot and durable queue pipeline in one database transaction.
/// </summary>
public sealed class UserDataRecoveryCoordinator : IUserDataRecoveryCoordinator
{
    private readonly IUserRepository _users;
    private readonly IUserCanonicalRecoveryStore _recoveryStore;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IUserSyncFaultRepository _faultRepository;
    private readonly IUserSyncFaultService _faults;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserDataBundleSyncService _bundleSync;
    private readonly IUserCanonicalHealthService _canonicalHealth;
    private readonly IUserCanonicalCheckpointRepository _checkpoints;
    private readonly IUserControlStateRepository _controlStates;
    private readonly IUserSnapshotPublisherService _publisher;
    private readonly ISyncQueueWriterService _queueWriter;
    private readonly IPendingSyncActivationService _activation;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IDeletedUserBarrierRepository _deletionBarriers;
    private readonly IDatabaseHealthService? _databaseHealth;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;
    private readonly TimeProvider _timeProvider;

    public UserDataRecoveryCoordinator(
        IUserRepository users,
        IUserSyncSnapshotRepository snapshots,
        IUserRevisionKnowledgeRepository knowledge,
        IUserSyncFaultRepository faultRepository,
        IUserSyncFaultService faults,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserDataBundleSyncService bundleSync,
        IUserCanonicalHealthService canonicalHealth,
        IUserCanonicalCheckpointRepository checkpoints,
        IUserControlStateRepository controlStates,
        IUserSnapshotPublisherService publisher,
        ISyncQueueWriterService queueWriter,
        IPendingSyncActivationService activation,
        IUserLoginIdentityProjectionService loginIdentities,
        IDeviceIdentityService identity,
        IUnitOfWork uow,
        IUserLifecycleCoordinator lifecycle,
        IDeletedUserBarrierRepository deletionBarriers,
        IInteractiveSessionStateService interactiveSessions,
        IUserCanonicalRecoveryStore recoveryStore,
        IDatabaseHealthService? databaseHealth = null,
        IUserTombstoneGarbageCollector? garbageCollector = null,
        TimeProvider? timeProvider = null)
    {
        _users = users;
        _recoveryStore = recoveryStore;
        _snapshots = snapshots;
        _knowledge = knowledge;
        _faultRepository = faultRepository;
        _faults = faults;
        _membershipAuthorization = membershipAuthorization;
        _bundleSync = bundleSync;
        _canonicalHealth = canonicalHealth;
        _checkpoints = checkpoints;
        _controlStates = controlStates;
        _publisher = publisher;
        _queueWriter = queueWriter;
        _activation = activation;
        _loginIdentities = loginIdentities;
        _identity = identity;
        _uow = uow;
        _lifecycle = lifecycle;
        _deletionBarriers = deletionBarriers;
        _interactiveSessions = interactiveSessions;
        _databaseHealth = databaseHealth;
        _garbageCollector = garbageCollector;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<UserDataRecoveryResult> TryRecoverAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        UserDataRecoveryTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (userId == Guid.Empty)
            return Task.FromResult(new UserDataRecoveryResult(UserDataRecoveryState.NothingToRecover, DiagnosticCode: "invalid-user-id"));

        return _lifecycle.ExecuteAsync(
            userId,
            token => TryRecoverUnderLifecycleAsync(userId, key, keyConfidence, trigger, token),
            ct);
    }

    public Task<byte[]?> TryResolvePasswordSaltAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return Task.FromResult<byte[]?>(null);

        return _lifecycle.ExecuteAsync(
            userId,
            token => TryResolvePasswordSaltUnderLifecycleAsync(userId, token),
            ct);
    }

    private async Task<byte[]?> TryResolvePasswordSaltUnderLifecycleAsync(
        Guid userId,
        CancellationToken ct)
    {
        if (await _deletionBarriers.ExistsAsync(userId, ct))
            return null;
        if (_databaseHealth is not null && !(await _databaseHealth.CheckAsync(ct)).IsHealthy)
            return null;

        var user = await _users.GetByIdWithRelationsAsync(userId, ct);
        if (user is null)
            return null;

        var faults = await _faultRepository.ListActiveForUserAsync(userId, ct);
        if (faults.Any(UserSyncFaultPolicy.IsTerminal) ||
            await _snapshots.HasIsolatedForkAsync(userId, ct))
            return null;

        var epochs = await ResolveAuthoritativeEpochsAsync(user, ct);
        if (!epochs.Success)
            return null;

        var salts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var row in await _snapshots.ListRecoveryEvidenceAsync(userId, epochs.KeyEpoch, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsEligibleEvidenceStatus(row.Status) ||
                await _faults.HasTerminalOriginFaultAsync(
                    row.UserId,
                    row.OriginDeviceId,
                    row.OriginInstanceId,
                    row.UserKeyEpoch,
                    ct))
                continue;

            try
            {
                var envelope = DeserializeAndValidateRow(row);
                if (envelope.UserKeyEpoch != epochs.KeyEpoch ||
                    envelope.MembershipEpoch <= 0 ||
                    envelope.MembershipEpoch > epochs.MembershipEpoch ||
                    envelope.User.PasswordSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
                    continue;

                await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
                var key = Convert.ToHexString(envelope.User.PasswordSalt);
                salts.TryAdd(key, envelope.User.PasswordSalt.ToArray());
                if (salts.Count > 1)
                    break;
            }
            catch (Exception ex) when (IsCandidateFailure(ex))
            {
                // Salt discovery is a pre-authentication hint only. Invalid evidence is ignored and
                // full recovery remains responsible for durable fault attribution.
            }
        }

        if (salts.Count == 1)
            return salts.Values.Single();

        foreach (var salt in salts.Values)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);
        return null;
    }

    private async Task<UserDataRecoveryResult> TryRecoverUnderLifecycleAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        UserDataRecoveryTrigger trigger,
        CancellationToken ct)
    {
        if (await _deletionBarriers.ExistsAsync(userId, ct))
            return new UserDataRecoveryResult(UserDataRecoveryState.AccountDeleted, DiagnosticCode: "account-deleted");

        if (_databaseHealth is not null)
        {
            var databaseHealth = await _databaseHealth.CheckAsync(ct);
            if (!databaseHealth.IsHealthy)
                return new UserDataRecoveryResult(UserDataRecoveryState.DatabaseUnhealthy, DiagnosticCode: databaseHealth.DiagnosticCode);
        }

        IUnitOfWorkTransaction? transaction = null;
        PreparedCanonicalRecovery? prepared = null;
        try
        {
            using var input = await _recoveryStore.CaptureAsync(userId, ct);
            if (input.Deleted)
                return new UserDataRecoveryResult(UserDataRecoveryState.AccountDeleted, DiagnosticCode: "account-deleted");
            if (input.PendingControl)
                return new UserDataRecoveryResult(UserDataRecoveryState.TransactionConflict, DiagnosticCode: "pending-security-operation");
            var user = input.User;
            if (user is null)
                return new UserDataRecoveryResult(UserDataRecoveryState.NothingToRecover, DiagnosticCode: "user-not-found");
            var activeFaults = input.Faults;
            if (activeFaults.Any(IsTerminalFork) || input.IsolatedFork)
                return new UserDataRecoveryResult(UserDataRecoveryState.TerminalFork, DiagnosticCode: "terminal-origin-fork");
            if (activeFaults.Any(IsControlPlaneConflict))
                return new UserDataRecoveryResult(UserDataRecoveryState.ControlPlaneConflict, DiagnosticCode: "terminal-control-plane-conflict");

            var localIdentity = CaptureLocalIdentity();
            var epochResolution = await ResolveAuthoritativeEpochsAsync(user, ct, input);
            if (!epochResolution.Success)
                return new UserDataRecoveryResult(epochResolution.State, DiagnosticCode: epochResolution.DiagnosticCode);
            var localFaults = activeFaults.Where(fault => fault.Scope == UserSyncFaultScope.LocalCanonical).ToList();
            var now = _timeProvider.GetUtcNow();
            var bypassBackoff = trigger is UserDataRecoveryTrigger.Login or UserDataRecoveryTrigger.RememberMeStartup or
                UserDataRecoveryTrigger.HealthyCandidateReceived or UserDataRecoveryTrigger.ManualRetry;
            if (localFaults.Count != 0 && !bypassBackoff && IsBackoffActive(localFaults, now))
                return new UserDataRecoveryResult(UserDataRecoveryState.BackoffActive, DiagnosticCode: "recovery-backoff-active");

            // Capture has already released SQLite's read snapshot. All costly work below reads
            // detached input only, without staging mutations or observing the shared item clock.
            var canonicalHealth = await _canonicalHealth.VerifyCapturedAsync(
                user, input.Checkpoint, key, keyConfidence, ct);
            var authenticated = new List<(UserSyncSnapshot Row, UserSnapshotEnvelope Envelope)>();
            var isolations = new List<RecoveryCandidateIsolation>();
            if (!canonicalHealth.FullyVerified)
            {
                foreach (var row in input.Candidates.Where(row => row.UserKeyEpoch == epochResolution.KeyEpoch))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsEligibleEvidenceStatus(row.Status) || activeFaults.Any(fault =>
                        UserSyncFaultPolicy.IsTerminal(fault) && fault.OriginDeviceId == row.OriginDeviceId &&
                        fault.OriginInstanceId == row.OriginInstanceId && fault.KeyEpoch == row.UserKeyEpoch))
                        continue;
                    try
                    {
                        var envelope = DeserializeAndValidateRow(row);
                        if (envelope.UserKeyEpoch != epochResolution.KeyEpoch || envelope.MembershipEpoch > epochResolution.MembershipEpoch)
                        {
                            isolations.Add(new(row, UserSyncFaultKind.InvalidEpoch, "recovery-candidate-epoch-invalid", UserDataBlobKind.All));
                            continue;
                        }
                        await _membershipAuthorization.VerifyCapturedSnapshotAuthorAsync(envelope, input.Authority, ct);
                        authenticated.Add((row, envelope));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        var code = ex.Message.Contains("cutoff", StringComparison.OrdinalIgnoreCase)
                            ? "recovery-candidate-post-removal-cutoff" : "recovery-candidate-unauthorized";
                        isolations.Add(new(row, UserSyncFaultKind.UnauthorizedOrigin, code, UserDataBlobKind.None));
                    }
                    catch (Exception ex) when (IsCandidateFailure(ex))
                    {
                        isolations.Add(new(row, UserSyncFaultKind.IncomingMetadataMismatch,
                            "recovery-candidate-envelope-invalid", UserDataBlobKind.None));
                    }
                }
            }

            Exception? preparationConflict = null;
            if (authenticated.Count != 0)
            {
                try
                {
                    prepared = await _bundleSync.PrepareCanonicalRecoveryAsync(user, input.Checkpoint,
                        authenticated.Select(candidate => candidate.Envelope).ToArray(), key, keyConfidence,
                        epochResolution.KeyEpoch, epochResolution.MembershipEpoch, ct);
                }
                catch (Exception ex) when (ex is RecoveryEvidenceConflictException or DeterministicSyncConflictException)
                {
                    // A speculative conflict is not a durable diagnosis until the exact inputs
                    // have been revalidated under the same writer lock as its fault transition.
                    preparationConflict = ex;
                }
            }
            ct.ThrowIfCancellationRequested();
            transaction = await _uow.BeginTransactionAsync(ct);
            if (await _deletionBarriers.ExistsAsync(userId, ct))
                throw new AccountDeletedDuringRecoveryException();
            if (!Hashing.Verify(localIdentity, CaptureLocalIdentity()) ||
                !await _recoveryStore.IsCurrentAsync(input, ct))
                throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(User));
            await _membershipAuthorization.VerifyLocalInstallationAsync(userId,
                epochResolution.KeyEpoch, epochResolution.MembershipEpoch, ct);

            if (preparationConflict is not null)
            {
                var code = preparationConflict is RecoveryEvidenceConflictException evidenceConflict
                    ? evidenceConflict.DiagnosticCode : "deterministic-recovery-conflict";
                await RecordPreparationConflictAsync(userId, code, preparationConflict is DeterministicSyncConflictException, ct);
                await _uow.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new UserDataRecoveryResult(UserDataRecoveryState.ControlPlaneConflict, DiagnosticCode: code);
            }

            if (canonicalHealth.FullyVerified)
            {
                if (localFaults.Count != 0)
                {
                    await _faults.MarkLocalCanonicalRecoveredAsync(userId, ct);
                    await _uow.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                else await transaction.RollbackAsync(ct);
                return new UserDataRecoveryResult(UserDataRecoveryState.NothingToRecover, DiagnosticCode: "canonical-already-healthy");
            }
            foreach (var isolation in isolations)
                await IsolateCandidateAsync(isolation.Row, isolation.Kind, isolation.DiagnosticCode, isolation.FailedBlobs, ct);

            if (authenticated.Count == 0)
            {
                if (!IsIndependentlyTrusted(keyConfidence) && localFaults.Count == 0)
                {
                    // Envelope/authentication failures are independently attributable and may be
                    // retained, but the supplied password is still not proof of local corruption.
                    await _uow.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return new UserDataRecoveryResult(UserDataRecoveryState.KeyNotTrusted, DiagnosticCode: "no-key-confirming-evidence");
                }

                localFaults = await EnsureLocalCanonicalFaultAsync(
                    user,
                    canonicalHealth,
                    epochResolution.KeyEpoch,
                    epochResolution.MembershipEpoch,
                    localFaults,
                    ct);
                MarkAttempt(localFaults, now, recovering: false);
                await _uow.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new UserDataRecoveryResult(UserDataRecoveryState.AwaitingPeerEvidence, DiagnosticCode: "no-authenticated-recovery-evidence");
            }

            var reconstruction = prepared!.Result;
            var keyConfirmed = IsIndependentlyTrusted(keyConfidence) || reconstruction.HealthyCandidateCount > 0;
            var resultByIdentity = reconstruction.Candidates
                .GroupBy(result => CandidateIdentity(
                    result.OriginDeviceId,
                    result.OriginInstanceId,
                    result.OriginRevision,
                    result.SnapshotHash), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            // Semantic decrypt/integrity attribution is safe only after this key has been confirmed
            // independently or by at least one complete healthy authenticated snapshot.
            if (keyConfirmed)
            {
                foreach (var candidate in authenticated)
                {
                    var identity = CandidateIdentity(
                        candidate.Row.OriginDeviceId,
                        candidate.Row.OriginInstanceId,
                        candidate.Row.OriginRevision,
                        candidate.Row.SnapshotHash);
                    if (!resultByIdentity.TryGetValue(identity, out var verification) || !verification.IsHealthy)
                    {
                        var failed = verification ?? new RecoveryCandidateVerificationResult(
                            candidate.Row.OriginDeviceId,
                            candidate.Row.OriginInstanceId,
                            candidate.Row.OriginRevision,
                            candidate.Row.SnapshotHash.ToArray(),
                            RecoveryCandidateState.IntegrityFailed,
                            UserDataBlobKind.All,
                            "recovery-candidate-verification-missing");
                        await IsolateCandidateAsync(
                            candidate.Row,
                            MapCandidateFault(failed.State),
                            failed.DiagnosticCode ?? "recovery-candidate-verification-failed",
                            failed.FailedComponents,
                            ct);
                    }
                }
            }

            if (!keyConfirmed)
            {
                await _uow.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new UserDataRecoveryResult(
                    UserDataRecoveryState.KeyNotTrusted,
                    reconstruction.HealthyCandidateCount,
                    DiagnosticCode: "password-key-not-confirmed");
            }


            localFaults = await EnsureLocalCanonicalFaultAsync(
                user,
                canonicalHealth,
                epochResolution.KeyEpoch,
                epochResolution.MembershipEpoch,
                localFaults,
                ct);
            MarkAttempt(localFaults, now, recovering: reconstruction.Reconstructed);

            if (!reconstruction.Reconstructed)
            {
                SetAwaitingEvidence(localFaults, now);
                await _uow.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new UserDataRecoveryResult(
                    UserDataRecoveryState.NoHealthyCandidate,
                    reconstruction.HealthyCandidateCount,
                    DiagnosticCode: reconstruction.DiagnosticCode);
            }

            user = await _users.GetByIdWithRelationsAsync(userId, ct)
                ?? throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(User));
            prepared.ApplyTo(user);
            _users.Update(user);
            foreach (var candidate in authenticated.Where(candidate =>
                         resultByIdentity.TryGetValue(
                             CandidateIdentity(
                                 candidate.Row.OriginDeviceId,
                                 candidate.Row.OriginInstanceId,
                                 candidate.Row.OriginRevision,
                                 candidate.Row.SnapshotHash),
                             out var result) && result.IsHealthy))
            {
                await RecordMergedKnowledgeAsync(candidate.Envelope, ct);
            }

            // Canonical replacement, checkpoint, local revision, coverage, queue, projection and
            // fault transitions all remain inside this transaction.
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
            // Publication reads coverage from storage inside this transaction.
            await _uow.SaveChangesAsync(ct);
            var localSnapshot = await _publisher.GetOrCreatePreparedRecoveryAsync(user, prepared, ct);
            await _queueWriter.EnqueueAsync(
                new SyncItem
                {
                    ModelId = user.UId,
                    ModelType = SyncModelType.User,
                    ChangeType = SyncChangeType.Updated,
                    ChangedAtTs = localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds()
                },
                localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds(),
                [],
                touchLocalSyncState: true,
                activateTargets: false,
                ct);

            foreach (var candidate in authenticated)
            {
                var immutableIdentity = CandidateIdentity(
                    candidate.Envelope.OriginDeviceId,
                    candidate.Envelope.OriginInstanceId,
                    candidate.Envelope.OriginRevision,
                    candidate.Envelope.SnapshotHash);
                if (!resultByIdentity.TryGetValue(immutableIdentity, out var verification) || !verification.IsHealthy)
                    continue;

                var isCurrentLocalOrigin = candidate.Envelope.OriginDeviceId == _identity.LocalDeviceId &&
                                           candidate.Envelope.OriginInstanceId == _identity.OriginInstanceId;
                if (!isCurrentLocalOrigin)
                {
                    var current = await _snapshots.GetExactAsync(userId, candidate.Row.OriginDeviceId,
                        candidate.Row.OriginInstanceId, candidate.Row.UserKeyEpoch, candidate.Row.OriginRevision, ct)
                        ?? throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(UserSyncSnapshot));
                    current.Status = UserSyncSnapshotStatus.MergedReceipt;
                    current.QuarantineReason = null;
                    current.ConflictingSnapshotHash = null;
                    _snapshots.Update(current);
                }

                await _faults.MarkOriginRecoveredAsync(
                    candidate.Envelope.UserId,
                    candidate.Envelope.OriginDeviceId,
                    candidate.Envelope.OriginInstanceId,
                    candidate.Envelope.UserKeyEpoch,
                    candidate.Envelope.OriginRevision,
                    ct);
            }

            foreach (var fault in localFaults)
            {
                fault.Status = UserSyncHealthStatus.Recovered;
                fault.RecoveredAtUtc = now;
                fault.NextRecoveryAttemptAtUtc = null;
                fault.SupersedingRevision = localSnapshot.OriginRevision;
                fault.BlocksPublishing = false;
                fault.BlocksMerge = false;
                fault.BlocksLogin = false;
                fault.BlocksGarbageCollection = false;
                fault.BlocksLifecycle = false;
                _faultRepository.Update(fault);
            }

            // The projection must see the final snapshot and fault states before commit.
            await _uow.SaveChangesAsync(ct);
            await _loginIdentities.RecalculateUnderLifecycleAsync(userId, ct);
            if (await _deletionBarriers.ExistsAsync(userId, ct))
                throw new AccountDeletedDuringRecoveryException();

            var finalControlState = await _controlStates.GetAsync(userId, ct);
            if (finalControlState is not null &&
                (finalControlState.HasConflict ||
                 (finalControlState.AppliedKeyEpoch > 0 && finalControlState.AppliedKeyEpoch != user.KeyEpoch) ||
                 (finalControlState.AppliedMembershipEpoch > 0 && finalControlState.AppliedMembershipEpoch != user.MembershipEpoch)))
            {
                throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(UserControlState));
            }

            await _membershipAuthorization.VerifyLocalInstallationAsync(user.UId, user.KeyEpoch, user.MembershipEpoch, ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            try { _bundleSync.ObserveCommittedRecovery(prepared); }
            catch
            {
                // Committed canonical reads observe this floor again before issuing item versions.
                // Clock persistence cannot turn a completed recovery into a failed attempt.
            }
            await RunPostCommitWorkAsync(user, key);
            return new UserDataRecoveryResult(
                UserDataRecoveryState.Recovered,
                reconstruction.HealthyCandidateCount,
                localSnapshot.OriginRevision,
                reconstruction.RecoveredComponents == UserDataBlobKind.All
                    ? "canonical-recovery-complete"
                    : "canonical-partial-salvage-complete");
        }
        catch (AccountDeletedDuringRecoveryException)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            return new UserDataRecoveryResult(UserDataRecoveryState.AccountDeleted, DiagnosticCode: "account-deleted-during-recovery");
        }
        catch (StorageWriteException ex) when (ex.Kind == StorageWriteException.FailureKind.StaleRow)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            return new UserDataRecoveryResult(UserDataRecoveryState.TransactionConflict, DiagnosticCode: "recovery-transaction-conflict");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            if (await _deletionBarriers.ExistsAsync(userId, ct))
                return new UserDataRecoveryResult(UserDataRecoveryState.AccountDeleted, DiagnosticCode: "account-deleted-during-recovery");
            await PersistRetryableFailureAsync(userId, ct);
            return new UserDataRecoveryResult(UserDataRecoveryState.Failed, DiagnosticCode: "canonical-recovery-failed");
        }
        finally
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { prepared?.Dispose(); }
        }
    }

    private async Task<EpochResolution> ResolveAuthoritativeEpochsAsync(User user, CancellationToken ct, UserRecoveryInput? input = null)
    {
        var control = input is null ? await _controlStates.GetAsync(user.UId, ct) : input.Control;
        if (control?.HasConflict == true)
            return EpochResolution.Failed(UserDataRecoveryState.ControlPlaneConflict, "control-state-conflict");

        UserCanonicalCheckpoint? checkpoint = input is null ? await _checkpoints.GetAsync(user.UId, ct) : input.Checkpoint;
        var checkpointAuthentic = false;
        if (checkpoint is not null)
        {
            try
            {
                UserCanonicalCheckpointUtil.VerifyAuthenticity(checkpoint, user.UId, _identity);
                checkpointAuthentic = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or
                                           System.Security.Cryptography.CryptographicException or
                                           ArgumentException)
            {
                // A damaged checkpoint is itself recoverable local evidence. It is never used to
                // select epochs unless its signature and local identity verify independently.
            }
        }

        var canonicalRowIntegrityVerified = false;
        try
        {
            user.VerifyIntegrity();
            canonicalRowIntegrityVerified = true;
        }
        catch (InvalidDataIntegrityException)
        {
        }

        var hasControlKeyEpoch = control is { AppliedKeyEpoch: > 0 };
        var hasControlMembershipEpoch = control is { AppliedMembershipEpoch: > 0 };
        var keyEpoch = hasControlKeyEpoch
            ? control!.AppliedKeyEpoch
            : checkpointAuthentic
                ? checkpoint!.KeyEpoch
                : canonicalRowIntegrityVerified
                    ? user.KeyEpoch
                    : 0;
        var membershipEpoch = hasControlMembershipEpoch
            ? control!.AppliedMembershipEpoch
            : checkpointAuthentic
                ? checkpoint!.MembershipEpoch
                : canonicalRowIntegrityVerified
                    ? user.MembershipEpoch
                    : 0;

        if (keyEpoch <= 0 || membershipEpoch <= 0)
            return EpochResolution.Failed(UserDataRecoveryState.WrongKeyEpoch, "authoritative-epoch-unavailable");

        if (checkpointAuthentic && control is not null &&
            ((control.AppliedKeyEpoch > 0 && control.AppliedKeyEpoch != checkpoint!.KeyEpoch) ||
             (control.AppliedMembershipEpoch > 0 && control.AppliedMembershipEpoch != checkpoint!.MembershipEpoch)))
        {
            return EpochResolution.Failed(UserDataRecoveryState.ControlPlaneConflict, "checkpoint-control-epoch-conflict");
        }

        try
        {
            if (input is null)
                await _membershipAuthorization.VerifyLocalInstallationAsync(user.UId, keyEpoch, membershipEpoch, ct);
            else
                await _membershipAuthorization.VerifyCapturedLocalInstallationAsync(user.UId, keyEpoch, membershipEpoch, input.Authority, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return EpochResolution.Failed(UserDataRecoveryState.LocalInstallationUnauthorized, "local-installation-unauthorized");
        }
        return new EpochResolution(true, keyEpoch, membershipEpoch, UserDataRecoveryState.NothingToRecover, null);
    }

    private async Task<List<UserSyncFault>> EnsureLocalCanonicalFaultAsync(
        User user,
        CanonicalHealthResult canonicalHealth,
        long keyEpoch,
        long membershipEpoch,
        List<UserSyncFault> existing,
        CancellationToken ct)
    {
        if (existing.Count != 0)
            return existing;

        var fault = await _faults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = CanonicalFaultKind(canonicalHealth.State),
            Status = UserSyncHealthStatus.AwaitingEvidence,
            AffectedComponent = canonicalHealth.FailedBlobs.ToString(),
            KeyEpoch = keyEpoch,
            MembershipEpoch = membershipEpoch,
            ExpectedHash = user.IntegrityHash,
            DiagnosticCode = canonicalHealth.DiagnosticCode,
            BlocksPublishing = true,
            BlocksMerge = false,
            BlocksLogin = true,
            BlocksGarbageCollection = true,
            BlocksLifecycle = true
        }, ct);
        return [fault];
    }

    private void MarkAttempt(IReadOnlyList<UserSyncFault> faults, DateTimeOffset now, bool recovering)
    {
        foreach (var fault in faults)
        {
            fault.Status = recovering ? UserSyncHealthStatus.Recovering : UserSyncHealthStatus.AwaitingEvidence;
            fault.RecoveryAttemptCount = checked(fault.RecoveryAttemptCount + 1);
            fault.LastRecoveryAttemptAtUtc = now;
            fault.NextRecoveryAttemptAtUtc = now.Add(CalculateBackoff(fault.RecoveryAttemptCount));
            _faultRepository.Update(fault);
        }
    }

    private bool IsBackoffActive(IEnumerable<UserSyncFault> faults, DateTimeOffset now) =>
        faults.Where(fault => fault.NextRecoveryAttemptAtUtc.HasValue)
            .Select(fault => fault.NextRecoveryAttemptAtUtc!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max() > now;

    private async Task RunPostCommitWorkAsync(User user, EncryptionKey key)
    {
        try
        {
            await _interactiveSessions.RefreshOrInvalidateUserSessionsAsync(
                user,
                CancellationToken.None);
        }
        catch
        {
            // Durable recovery is complete; session handling fails closed inside the service.
        }

        if (_garbageCollector is not null)
        {
            try
            {
                await _garbageCollector.CollectAsync(user.UId, key, CancellationToken.None);
            }
            catch
            {
                // Conservative GC can be retried after recovery.
            }
        }

        try
        {
            await _activation.ActivatePendingAsync(CancellationToken.None);
        }
        catch
        {
            // Queue rows are durable and normal activation retry remains authoritative.
        }
    }

    private async Task IsolateCandidateAsync(
        UserSyncSnapshot row,
        UserSyncFaultKind kind,
        string diagnosticCode,
        UserDataBlobKind failedBlobs,
        CancellationToken ct)
    {
        row = await _snapshots.GetExactAsync(row.UserId, row.OriginDeviceId, row.OriginInstanceId,
            row.UserKeyEpoch, row.OriginRevision, ct)
            ?? throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, typeof(UserSyncSnapshot));
        row.Status = kind == UserSyncFaultKind.SameRevisionFork
            ? UserSyncSnapshotStatus.IsolatedFork
            : UserSyncSnapshotStatus.IsolatedCorrupt;
        row.QuarantineReason = diagnosticCode;
        row.ConflictingSnapshotHash = null;
        _snapshots.Update(row);
        await _faults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = row.UserId,
            Scope = kind == UserSyncFaultKind.SameRevisionFork
                ? UserSyncFaultScope.SnapshotFork
                : UserSyncFaultScope.SnapshotOrigin,
            Kind = kind,
            Status = kind == UserSyncFaultKind.SameRevisionFork
                ? UserSyncHealthStatus.TerminalConflict
                : UserSyncHealthStatus.Isolated,
            AffectedComponent = failedBlobs == UserDataBlobKind.None ? "snapshot-envelope" : failedBlobs.ToString(),
            OriginDeviceId = row.OriginDeviceId,
            OriginInstanceId = row.OriginInstanceId,
            KeyEpoch = row.UserKeyEpoch,
            MembershipEpoch = row.MembershipEpoch,
            OriginRevision = row.OriginRevision,
            ObservedHash = row.SnapshotHash,
            DiagnosticCode = diagnosticCode,
            BlocksMerge = true,
            BlocksGarbageCollection = true,
            BlocksLogin = kind == UserSyncFaultKind.SameRevisionFork
        }, ct);
    }

    private async Task RecordMergedKnowledgeAsync(UserSnapshotEnvelope envelope, CancellationToken ct)
    {
        await UpsertMergedAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.UserKeyEpoch,
            envelope.OriginRevision,
            envelope.SnapshotHash,
            ct);

        foreach (var covered in envelope.Coverage
                     .Where(item => item.UserKeyEpoch <= envelope.UserKeyEpoch && item.OriginRevision > 0)
                     .OrderBy(item => item.OriginDeviceId)
                     .ThenBy(item => item.OriginInstanceId)
                     .ThenBy(item => item.UserKeyEpoch))
        {
            await UpsertMergedAsync(
                envelope.UserId,
                covered.OriginDeviceId,
                covered.OriginInstanceId,
                covered.UserKeyEpoch,
                covered.OriginRevision,
                [],
                ct);
        }
    }

    private async Task UpsertMergedAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long keyEpoch,
        long revision,
        byte[] snapshotHash,
        CancellationToken ct)
    {
        var item = await _knowledge.GetAsync(userId, originDeviceId, originInstanceId, keyEpoch, ct);
        var isNew = item is null;
        item ??= new UserRevisionKnowledge
        {
            UserId = userId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = keyEpoch
        };

        if (snapshotHash.Length == Constants.SyncConstants.SyncDeltaPayloadHashBytes &&
            revision > item.HighestStoredRevision)
        {
            item.HighestStoredRevision = revision;
            item.HighestStoredSnapshotHash = snapshotHash.ToArray();
        }
        item.HighestMergedRevision = Math.Max(item.HighestMergedRevision, revision);
        item.LastUpdatedAtUtc = _timeProvider.GetUtcNow();
        if (isNew)
            await _knowledge.AddAsync(item, ct);
        else
            _knowledge.Update(item);
    }

    private async Task PersistRetryableFailureAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await using var retry = await _uow.BeginTransactionAsync(ct);
            if (await _deletionBarriers.ExistsAsync(userId, ct) || !await _users.ExistsAsync(userId, ct))
                return;
            var faults = await _faultRepository.ListActiveForUserAsync(userId, ct);
            var now = _timeProvider.GetUtcNow();
            foreach (var fault in faults.Where(UserSyncFaultPolicy.IsRecoverableCanonical))
            {
                fault.Status = UserSyncHealthStatus.AwaitingEvidence;
                fault.LastRecoveryAttemptAtUtc ??= now;
                fault.RecoveryAttemptCount = Math.Max(1, fault.RecoveryAttemptCount);
                fault.NextRecoveryAttemptAtUtc = now.Add(CalculateBackoff(fault.RecoveryAttemptCount));
                _faultRepository.Update(fault);
            }
            await _uow.SaveChangesAsync(ct);
            await retry.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _uow.ClearTrackedChanges();
            throw;
        }
        catch { _uow.ClearTrackedChanges(); }
    }

    private byte[] CaptureLocalIdentity() => Hashing.SHA256Hash(hash =>
    {
        hash.Write(_identity.IsInitialized);
        hash.Write(_identity.LocalDeviceId);
        hash.Write(_identity.OriginInstanceId);
        hash.WriteBytes(_identity.SignPublicKey);
        hash.WriteBytes(_identity.AgreementPublicKey);
        hash.WriteString(_identity.FingerprintHex);
        hash.Write((byte)_identity.DeviceType);
    });

    private async Task RecordPreparationConflictAsync(Guid userId, string diagnosticCode, bool deterministic, CancellationToken ct)
    {
        await _faults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = userId, Scope = deterministic ? UserSyncFaultScope.DeterministicItem : UserSyncFaultScope.ControlPlane,
            Kind = deterministic ? UserSyncFaultKind.DeterministicItemConflict : UserSyncFaultKind.KeyEpochConflict,
            Status = UserSyncHealthStatus.TerminalConflict,
            AffectedComponent = deterministic ? "recovery-item-version" : "recovery-key-material", DiagnosticCode = diagnosticCode,
            BlocksPublishing = true, BlocksMerge = true, BlocksLogin = true,
            BlocksGarbageCollection = true, BlocksLifecycle = true
        }, ct);
        foreach (var fault in (await _faultRepository.ListActiveForUserAsync(userId, ct))
                     .Where(UserSyncFaultPolicy.IsRecoverableCanonical))
        {
            fault.Status = UserSyncHealthStatus.TerminalConflict;
            fault.DiagnosticCode = diagnosticCode;
            fault.NextRecoveryAttemptAtUtc = null;
            _faultRepository.Update(fault);
        }
    }

    private void SetAwaitingEvidence(IEnumerable<UserSyncFault> faults, DateTimeOffset now)
    {
        foreach (var fault in faults)
        {
            fault.Status = UserSyncHealthStatus.AwaitingEvidence;
            fault.LastRecoveryAttemptAtUtc ??= now;
            _faultRepository.Update(fault);
        }
    }

    private TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var seconds = Math.Min(6 * 60 * 60, 30 * (1 << exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private bool IsEligibleEvidenceStatus(UserSyncSnapshotStatus status) =>
        status is UserSyncSnapshotStatus.Pending or
            UserSyncSnapshotStatus.RecoveryCandidate or
            UserSyncSnapshotStatus.MergedReceipt or
            UserSyncSnapshotStatus.LocalPublished;

    private bool IsIndependentlyTrusted(UserSyncKeyConfidence confidence) =>
        confidence != UserSyncKeyConfidence.UnconfirmedPassword;

    private bool IsTerminalFork(UserSyncFault fault) =>
        UserSyncFaultPolicy.IsTerminal(fault) &&
        (fault.Scope == UserSyncFaultScope.SnapshotFork ||
         fault.Kind is UserSyncFaultKind.SameRevisionFork or
             UserSyncFaultKind.RevisionRollback or
             UserSyncFaultKind.DuplicateOriginInstallation);

    private bool IsControlPlaneConflict(UserSyncFault fault) =>
        UserSyncFaultPolicy.IsTerminal(fault) && !IsTerminalFork(fault);

    private UserSyncFaultKind CanonicalFaultKind(UserDataVerificationState state) => state switch
    {
        UserDataVerificationState.RowIntegrityFailure => UserSyncFaultKind.CanonicalIntegrityMismatch,
        UserDataVerificationState.CheckpointMissing => UserSyncFaultKind.CanonicalCheckpointMissing,
        UserDataVerificationState.CheckpointFailure => UserSyncFaultKind.CanonicalCheckpointMismatch,
        UserDataVerificationState.RootDecryptFailure => UserSyncFaultKind.CanonicalRootDecryptFailure,
        UserDataVerificationState.RootIntegrityFailure => UserSyncFaultKind.CanonicalRootIntegrityFailure,
        UserDataVerificationState.GeneralBlobFailure => UserSyncFaultKind.CanonicalGeneralBlobFailure,
        UserDataVerificationState.PasswordsBlobFailure => UserSyncFaultKind.CanonicalPasswordsBlobFailure,
        UserDataVerificationState.DevicesBlobFailure => UserSyncFaultKind.CanonicalDevicesBlobFailure,
        UserDataVerificationState.BundleLinkFailure => UserSyncFaultKind.CanonicalBundleLinkFailure,
        UserDataVerificationState.LoginMetadataFailure => UserSyncFaultKind.CanonicalGeneralBlobFailure,
        _ => UserSyncFaultKind.CanonicalIntegrityMismatch
    };

    private UserSyncFaultKind MapCandidateFault(RecoveryCandidateState state) => state switch
    {
        RecoveryCandidateState.DecryptFailed => UserSyncFaultKind.IncomingDecryptFailure,
        RecoveryCandidateState.UnauthorizedOrigin or RecoveryCandidateState.PostRemovalCutoff => UserSyncFaultKind.UnauthorizedOrigin,
        RecoveryCandidateState.WrongKeyEpoch => UserSyncFaultKind.InvalidEpoch,
        RecoveryCandidateState.MetadataMismatch or RecoveryCandidateState.InvalidEnvelope or RecoveryCandidateState.InvalidSignature => UserSyncFaultKind.IncomingMetadataMismatch,
        RecoveryCandidateState.Forked => UserSyncFaultKind.SameRevisionFork,
        RecoveryCandidateState.ControlPlaneConflict => UserSyncFaultKind.KeyEpochConflict,
        _ => UserSyncFaultKind.IncomingIntegrityFailure
    };

    private string CandidateIdentity(Guid deviceId, Guid instanceId, long revision, byte[] snapshotHash) =>
        $"{deviceId:N}:{instanceId:N}:{revision}:{Convert.ToHexString(snapshotHash)}";

    private UserSnapshotEnvelope DeserializeAndValidateRow(UserSyncSnapshot row)
    {
        if (row.EnvelopePayload.Length == 0 || row.EnvelopePayload.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The retained recovery envelope size is invalid.");

        var envelope = JsonSerializer.Deserialize(
                           row.EnvelopePayload,
                           BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
                       ?? throw new InvalidDataException("The retained recovery envelope is invalid.");
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (row.UserId != envelope.UserId ||
            row.OriginDeviceId != envelope.OriginDeviceId ||
            row.OriginInstanceId != envelope.OriginInstanceId ||
            row.OriginRevision != envelope.OriginRevision ||
            row.UserKeyEpoch != envelope.UserKeyEpoch ||
            row.MembershipEpoch != envelope.MembershipEpoch ||
            row.CreatedAtUtc != envelope.CreatedAtUtc ||
            !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash) ||
            !Hashing.Verify(row.OriginSignPublicKey, envelope.OriginSignPublicKey) ||
            !Hashing.Verify(row.OriginSignature, envelope.OriginSignature))
        {
            throw new InvalidDataException("The retained recovery row conflicts with its immutable envelope.");
        }
        return envelope;
    }

    private bool IsCandidateFailure(Exception ex) =>
        ex is InvalidDataException or
            UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or
            InvalidDataIntegrityException or
            JsonException;


}
