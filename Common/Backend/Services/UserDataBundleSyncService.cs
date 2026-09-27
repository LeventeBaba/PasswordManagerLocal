using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;
namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDataBundleSyncService : IUserDataBundleSyncService
{
    private readonly IUserRepository _users;
    private readonly IUserDataBundleIntegrityService _integrity;
    private readonly IUserPasswordsDataMergeService _passwordsDataMerge;
    private readonly IUserDevicesDataMergeService _devicesDataMerge;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserDataBundleVerificationService _verification;
    private readonly IUserSnapshotBatchVerificationService _snapshotBatchVerification;
    private readonly IUserCanonicalCheckpointRepository? _checkpoints;
    private readonly IDeviceIdentityService? _identity;

    public UserDataBundleSyncService(
        IUserRepository users,
        IUserDataBundleIntegrityService integrity,
        IUserPasswordsDataMergeService passwordsDataMerge,
        IUserDevicesDataMergeService devicesDataMerge,
        ISyncVersionClockService versionClock,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserDataBundleVerificationService? verification = null,
        IUserCanonicalCheckpointRepository? checkpoints = null,
        IDeviceIdentityService? identity = null,
        IUserSnapshotBatchVerificationService? snapshotBatchVerification = null)
    {
        _users = users;
        _integrity = integrity;
        _passwordsDataMerge = passwordsDataMerge;
        _devicesDataMerge = devicesDataMerge;
        _versionClock = versionClock;
        _canonicalHealth = canonicalHealth;
        _verification = verification ?? new UserDataBundleVerificationService(integrity);
        _snapshotBatchVerification = snapshotBatchVerification ??
            new UserSnapshotBatchVerificationService(_verification);
        _checkpoints = checkpoints;
        _identity = identity;
    }

    public Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        CancellationToken ct = default) =>
        TryVerifyAndMergeManyAsync(existing, snapshots, key, UserSyncKeyConfidence.ExplicitlyTrusted, ct);

    public async Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return new UserSnapshotMergeBatchResult(false, []);

        var ordered = snapshots
            .OrderBy(snapshot => snapshot.OriginDeviceId)
            .ThenBy(snapshot => snapshot.OriginInstanceId)
            .ThenBy(snapshot => snapshot.OriginRevision)
            .ToArray();

        var canonicalVerification = await _verification.VerifyCanonicalAsync(existing, key, keyConfidence, ct);
        if (!canonicalVerification.IsHealthy)
        {
            var candidateResults = new List<UserSnapshotMergeEntryResult>(ordered.Length);
            var candidateVerifications = await _snapshotBatchVerification.VerifyAsync(
                ordered,
                key,
                keyConfidence,
                ct);
            try
            {
                for (var index = 0; index < ordered.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var snapshot = ordered[index];
                    var verification = candidateVerifications[index];
                    if (verification.IsHealthy)
                    {
                        candidateResults.Add(new UserSnapshotMergeEntryResult(
                            snapshot.OriginDeviceId,
                            snapshot.OriginInstanceId,
                            snapshot.OriginRevision,
                            true,
                            VerificationState: UserDataVerificationState.Healthy,
                            DiagnosticCode: "healthy-recovery-evidence"));
                    }
                    else
                    {
                        candidateResults.Add(Failed(snapshot, verification));
                    }
                }
            }
            finally
            {
                DisposeVerificationResults(candidateVerifications);
            }

            return new UserSnapshotMergeBatchResult(
                false,
                candidateResults,
                canonicalVerification.State,
                canonicalVerification.FailedBlobs,
                canonicalVerification.DiagnosticCode);
        }

        var canonicalBundle = canonicalVerification.VerifiedBundle!;
        IReadOnlyList<UserDataBundleVerificationResult> incomingVerifications = [];
        var results = new List<UserSnapshotMergeEntryResult>(ordered.Length);
        var changedBlobs = UserDataBlobKind.None;
        var anyVerified = false;

        try
        {
            var verificationCandidates = ordered
                .Where(snapshot => existing.PasswordSalt.SequenceEqual(snapshot.User.PasswordSalt))
                .ToArray();
            incomingVerifications = await _snapshotBatchVerification.VerifyAsync(
                verificationCandidates,
                key,
                keyConfidence == UserSyncKeyConfidence.UnconfirmedPassword
                    ? UserSyncKeyConfidence.VerifiedRemoteSnapshot
                    : keyConfidence,
                ct);
            var verificationIndex = 0;

            foreach (var snapshot in ordered)
            {
                ct.ThrowIfCancellationRequested();

                if (!existing.PasswordSalt.SequenceEqual(snapshot.User.PasswordSalt))
                {
                    results.Add(new UserSnapshotMergeEntryResult(
                        snapshot.OriginDeviceId,
                        snapshot.OriginInstanceId,
                        snapshot.OriginRevision,
                        false,
                        "The snapshot password salt does not match the active key epoch.",
                        UserDataVerificationState.EpochFailure,
                        UserDataBlobKind.All,
                        "password-salt-mismatch"));
                    continue;
                }

                var incomingVerification = incomingVerifications[verificationIndex++];
                if (!incomingVerification.IsHealthy)
                {
                    results.Add(Failed(snapshot, incomingVerification));
                    continue;
                }

                var incomingBundle = incomingVerification.VerifiedBundle!;
                _versionClock.Observe(SyncVersionStampTraversal.Enumerate(incomingBundle));
                anyVerified = true;
                var snapshotChangedBlobs = UserDataBlobKind.None;

                try
                {
                    if (GeneralUserDataMergeUtil.Merge(
                            canonicalBundle.GeneralUserData,
                            incomingBundle.GeneralUserData,
                            existing,
                            snapshot.User))
                        snapshotChangedBlobs |= UserDataBlobKind.General;
                    if (_passwordsDataMerge.Merge(canonicalBundle.UserPasswordsData, incomingBundle.UserPasswordsData))
                        snapshotChangedBlobs |= UserDataBlobKind.Passwords;
                    if (_devicesDataMerge.Merge(canonicalBundle.UserDevicesData, incomingBundle.UserDevicesData))
                        snapshotChangedBlobs |= UserDataBlobKind.Devices;
                }
                catch (DeterministicSyncConflictException ex) when (!ex.UserId.HasValue)
                {
                    throw ex.WithUserId(existing.UId);
                }

                changedBlobs |= snapshotChangedBlobs;
                var snapshotTimestamp = snapshot.CreatedAtUtc.ToUniversalTime();
                if (snapshotChangedBlobs != UserDataBlobKind.None)
                {
                    existing.UserDataLastModifiedAt = MaxDateTimeOffset(
                        existing.UserDataLastModifiedAt,
                        snapshot.User.UserDataLastModifiedAt,
                        snapshotTimestamp);
                }
                if (snapshotChangedBlobs.HasFlag(UserDataBlobKind.General))
                {
                    existing.GeneralUserDataLastModifiedAt = MaxDateTimeOffset(
                        existing.GeneralUserDataLastModifiedAt,
                        snapshot.User.GeneralUserDataLastModifiedAt,
                        snapshotTimestamp);
                }
                if (snapshotChangedBlobs.HasFlag(UserDataBlobKind.Passwords))
                {
                    existing.UserPasswordsDataLastModifiedAt = MaxDateTimeOffset(
                        existing.UserPasswordsDataLastModifiedAt,
                        snapshot.User.UserPasswordsDataLastModifiedAt,
                        snapshotTimestamp);
                }
                if (snapshotChangedBlobs.HasFlag(UserDataBlobKind.Devices))
                {
                    existing.UserDevicesDataLastModifiedAt = MaxDateTimeOffset(
                        existing.UserDevicesDataLastModifiedAt,
                        snapshot.User.UserDevicesDataLastModifiedAt,
                        snapshotTimestamp);
                }

                existing.LastModifiedAt = MaxDateTimeOffset(existing.LastModifiedAt, snapshotTimestamp);
                results.Add(new UserSnapshotMergeEntryResult(
                    snapshot.OriginDeviceId,
                    snapshot.OriginInstanceId,
                    snapshot.OriginRevision,
                    true,
                    VerificationState: UserDataVerificationState.Healthy,
                    DiagnosticCode: "healthy"));
            }

            if (!anyVerified)
                return new UserSnapshotMergeBatchResult(false, results);

            if (changedBlobs != UserDataBlobKind.None)
                await PersistMergedUserBundleAsync(existing, canonicalBundle, key, changedBlobs, ct);

            existing.GenerateIntegrityHash();
            _users.Update(existing);
            if (_canonicalHealth is not null)
                await _canonicalHealth.UpdateCheckpointAsync(existing, ct);
            return new UserSnapshotMergeBatchResult(changedBlobs != UserDataBlobKind.None, results);
        }
        finally
        {
            canonicalBundle.Dispose();
            DisposeVerificationResults(incomingVerifications);
        }
    }

    public Task<UserDataRecoveryReconstructionResult> TryReconstructCanonicalAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        TryReconstructCanonicalAsync(
            existing,
            snapshots,
            key,
            keyConfidence,
            existing.KeyEpoch,
            existing.MembershipEpoch,
            ct);

    public async Task<UserDataRecoveryReconstructionResult> TryReconstructCanonicalAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        long expectedKeyEpoch,
        long expectedMembershipEpoch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(key);
        if (snapshots.Count == 0)
            return new UserDataRecoveryReconstructionResult(false, [], "no-recovery-evidence");
        if (expectedKeyEpoch <= 0 || expectedMembershipEpoch <= 0)
            return new UserDataRecoveryReconstructionResult(false, [], "recovery-authoritative-epoch-invalid");

        // Stable immutable ordering makes reconstruction independent from database row order,
        // receive order and relay path. Exact duplicate envelopes are processed once.
        var ordered = snapshots
            .OrderBy(snapshot => snapshot.OriginDeviceId)
            .ThenBy(snapshot => snapshot.OriginInstanceId)
            .ThenBy(snapshot => snapshot.OriginRevision)
            .ThenBy(snapshot => Convert.ToHexString(snapshot.SnapshotHash), StringComparer.Ordinal)
            .GroupBy(
                snapshot => $"{snapshot.OriginDeviceId:N}:{snapshot.OriginInstanceId:N}:" +
                            $"{snapshot.OriginRevision}:{Convert.ToHexString(snapshot.SnapshotHash)}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var verified = new List<(UserSnapshotEnvelope Envelope, UserDataBundleVerificationResult Verification)>();
        var candidateResults = new List<RecoveryCandidateVerificationResult>(ordered.Length);
        var prevalidationFailures = new RecoveryCandidateVerificationResult?[ordered.Length];
        var verificationCandidates = new List<UserSnapshotEnvelope>(ordered.Length);
        byte[]? epochPasswordSalt = null;
        CanonicalSalvage? localSalvage = null;
        UserSyncPayload? localMetadata = null;
        IReadOnlyList<UserDataBundleVerificationResult> candidateVerifications = [];

        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshot = ordered[index];
            if (snapshot.UserId != existing.UId ||
                snapshot.User.UId != existing.UId ||
                snapshot.UserKeyEpoch != expectedKeyEpoch ||
                snapshot.MembershipEpoch <= 0 ||
                snapshot.MembershipEpoch > expectedMembershipEpoch)
            {
                prevalidationFailures[index] = CandidateFailure(
                    snapshot,
                    RecoveryCandidateState.WrongKeyEpoch,
                    UserDataBlobKind.All,
                    "recovery-epoch-mismatch");
                continue;
            }

            if (snapshot.User.PasswordSalt.Length == 0)
            {
                prevalidationFailures[index] = CandidateFailure(
                    snapshot,
                    RecoveryCandidateState.WrongKeyEpoch,
                    UserDataBlobKind.All,
                    "recovery-password-salt-missing");
                continue;
            }

            verificationCandidates.Add(snapshot);
        }

        try
        {
            candidateVerifications = await _snapshotBatchVerification.VerifyAsync(
                verificationCandidates,
                key,
                keyConfidence == UserSyncKeyConfidence.UnconfirmedPassword
                    ? UserSyncKeyConfidence.VerifiedRemoteSnapshot
                    : keyConfidence,
                ct);
            var verificationIndex = 0;

            for (var index = 0; index < ordered.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = ordered[index];
                if (prevalidationFailures[index] is { } prevalidationFailure)
                {
                    candidateResults.Add(prevalidationFailure);
                    continue;
                }

                var verification = candidateVerifications[verificationIndex++];
                if (!verification.IsHealthy)
                {
                    candidateResults.Add(CandidateFailure(
                        snapshot,
                        MapRecoveryCandidateState(verification.State),
                        verification.FailedBlobs,
                        verification.DiagnosticCode));
                    continue;
                }

                var bundle = verification.VerifiedBundle!;
                if (bundle.UserData.FormatVersion != Constants.SyncConstants.EncryptedUserDataFormatVersion ||
                    bundle.UserData.UId != existing.UId)
                {
                    candidateResults.Add(CandidateFailure(
                        snapshot,
                        RecoveryCandidateState.IntegrityFailed,
                        UserDataBlobKind.All,
                        "recovery-root-format-invalid"));
                    continue;
                }

                // Password salt and root child-key material are immutable within one key epoch.
                // Individually healthy snapshots that disagree here represent an unresolved
                // control-plane branch, not evidence from which recovery may pick a winner.
                if (epochPasswordSalt is null)
                {
                    epochPasswordSalt = snapshot.User.PasswordSalt.ToArray();
                }
                else if (!Hashing.Verify(epochPasswordSalt, snapshot.User.PasswordSalt))
                {
                    throw new RecoveryEvidenceConflictException("recovery-password-salt-conflict");
                }

                if (verified.Count != 0 &&
                    !HasSameRootKeyMaterial(verified[0].Verification.VerifiedBundle!.UserData, bundle.UserData))
                {
                    throw new RecoveryEvidenceConflictException("recovery-root-key-material-conflict");
                }

                verified.Add((snapshot, verification));
                candidateResults.Add(new RecoveryCandidateVerificationResult(
                    snapshot.OriginDeviceId,
                    snapshot.OriginInstanceId,
                    snapshot.OriginRevision,
                    snapshot.SnapshotHash.ToArray(),
                    RecoveryCandidateState.Healthy,
                    DiagnosticCode: "healthy-recovery-evidence"));
            }

            if (verified.Count == 0)
                return new UserDataRecoveryReconstructionResult(false, candidateResults, "no-healthy-recovery-candidate");

            // Partial salvage is allowed only when the signed canonical checkpoint verifies, the
            // root decrypts and verifies, and individual child components
            // can be independently authenticated through the root's child hashes.
            localSalvage = await TryReadCanonicalSalvageAsync(
                existing,
                key,
                expectedKeyEpoch,
                expectedMembershipEpoch,
                ct);
            localMetadata = CreateRecoveryMetadata(existing, localSalvage?.General);
            if (localSalvage is not null &&
                !HasSameRootKeyMaterial(localSalvage.Root, verified[0].Verification.VerifiedBundle!.UserData))
            {
                throw new RecoveryEvidenceConflictException("canonical-candidate-root-key-material-conflict");
            }

            var baseCandidate = verified[0];
            var baseBundle = baseCandidate.Verification.VerifiedBundle!;
            CopyRecoveryMetadata(baseCandidate.Envelope.User, existing);
            existing.KeyEpoch = expectedKeyEpoch;
            existing.MembershipEpoch = expectedMembershipEpoch;
            existing.LastModifiedAt = baseCandidate.Envelope.CreatedAtUtc;

            _versionClock.Observe(SyncVersionStampTraversal.Enumerate(baseBundle));
            for (var index = 1; index < verified.Count; index++)
            {
                MergeRecoveryBundle(
                    existing,
                    baseBundle,
                    verified[index].Verification.VerifiedBundle!,
                    verified[index].Envelope.User,
                    verified[index].Envelope.CreatedAtUtc);
            }

            var recoveredComponents = UserDataBlobKind.All;
            if (localSalvage is not null)
            {
                recoveredComponents = localSalvage.FailedComponents;
                MergeHealthyLocalSalvage(existing, baseBundle, localSalvage, localMetadata);
                existing.LastModifiedAt = MaxDateTimeOffset(existing.LastModifiedAt, localSalvage.LastModifiedAt);
            }

            // Recovery is reconstruction, not a user mutation: item/version stamps are retained.
            // Aggregate child hashes, root links and local encryption are rebuilt around the
            // deterministic logical result and then verified again before persistence.
            _integrity.RebuildModifiedBlobIntegrity(baseBundle, UserDataBlobKind.All);
            _integrity.VerifyUntrustedBundle(baseBundle);
            UserLoginIdentityMetadataUtil.Verify(existing, baseBundle.GeneralUserData);
            await PersistMergedUserBundleAsync(existing, baseBundle, key, UserDataBlobKind.All, ct);
            existing.GenerateIntegrityHash();
            existing.VerifyIntegrity();
            _users.Update(existing);

            return new UserDataRecoveryReconstructionResult(true, candidateResults, "canonical-reconstructed")
            {
                RecoveredComponents = recoveredComponents == UserDataBlobKind.None
                    ? UserDataBlobKind.All
                    : recoveredComponents
            };
        }
        finally
        {
            localSalvage?.Dispose();
            if (epochPasswordSalt is not null)
                CryptographicOperations.ZeroMemory(epochPasswordSalt);
            DisposeVerificationResults(candidateVerifications);
        }
    }

    private void MergeRecoveryBundle(
        User existing,
        UserDataBundle target,
        UserDataBundle incoming,
        UserSyncPayload incomingMetadata,
        DateTimeOffset incomingTimestamp)
    {
        _versionClock.Observe(SyncVersionStampTraversal.Enumerate(incoming));
        var changed = UserDataBlobKind.None;
        try
        {
            if (GeneralUserDataMergeUtil.Merge(target.GeneralUserData, incoming.GeneralUserData, existing, incomingMetadata))
                changed |= UserDataBlobKind.General;
            if (_passwordsDataMerge.Merge(target.UserPasswordsData, incoming.UserPasswordsData))
                changed |= UserDataBlobKind.Passwords;
            if (_devicesDataMerge.Merge(target.UserDevicesData, incoming.UserDevicesData))
                changed |= UserDataBlobKind.Devices;
        }
        catch (DeterministicSyncConflictException ex) when (!ex.UserId.HasValue)
        {
            throw ex.WithUserId(existing.UId);
        }

        existing.UserDataLastModifiedAt = MaxDateTimeOffset(
            existing.UserDataLastModifiedAt,
            incomingMetadata.UserDataLastModifiedAt,
            incomingTimestamp);
        if (changed.HasFlag(UserDataBlobKind.General))
        {
            existing.GeneralUserDataLastModifiedAt = MaxDateTimeOffset(
                existing.GeneralUserDataLastModifiedAt,
                incomingMetadata.GeneralUserDataLastModifiedAt,
                incomingTimestamp);
        }
        if (changed.HasFlag(UserDataBlobKind.Passwords))
        {
            existing.UserPasswordsDataLastModifiedAt = MaxDateTimeOffset(
                existing.UserPasswordsDataLastModifiedAt,
                incomingMetadata.UserPasswordsDataLastModifiedAt,
                incomingTimestamp);
        }
        if (changed.HasFlag(UserDataBlobKind.Devices))
        {
            existing.UserDevicesDataLastModifiedAt = MaxDateTimeOffset(
                existing.UserDevicesDataLastModifiedAt,
                incomingMetadata.UserDevicesDataLastModifiedAt,
                incomingTimestamp);
        }
        existing.LastModifiedAt = MaxDateTimeOffset(existing.LastModifiedAt, incomingTimestamp);
    }

    private void MergeHealthyLocalSalvage(
        User existing,
        UserDataBundle target,
        CanonicalSalvage local,
        UserSyncPayload localMetadata)
    {
        try
        {
            if (local.General is not null)
            {
                _versionClock.Observe([local.General.Version]);
                GeneralUserDataMergeUtil.Merge(target.GeneralUserData, local.General, existing, localMetadata);
                existing.GeneralUserDataLastModifiedAt = MaxDateTimeOffset(
                    existing.GeneralUserDataLastModifiedAt,
                    localMetadata.GeneralUserDataLastModifiedAt);
            }
            if (local.Passwords is not null)
            {
                _versionClock.Observe(SyncVersionStampTraversal.Enumerate(local.Passwords));
                _passwordsDataMerge.Merge(target.UserPasswordsData, local.Passwords);
                existing.UserPasswordsDataLastModifiedAt = MaxDateTimeOffset(
                    existing.UserPasswordsDataLastModifiedAt,
                    localMetadata.UserPasswordsDataLastModifiedAt);
            }
            if (local.Devices is not null)
            {
                _versionClock.Observe(SyncVersionStampTraversal.Enumerate(local.Devices));
                _devicesDataMerge.Merge(target.UserDevicesData, local.Devices);
                existing.UserDevicesDataLastModifiedAt = MaxDateTimeOffset(
                    existing.UserDevicesDataLastModifiedAt,
                    localMetadata.UserDevicesDataLastModifiedAt);
            }
        }
        catch (DeterministicSyncConflictException ex) when (!ex.UserId.HasValue)
        {
            throw ex.WithUserId(existing.UId);
        }

        existing.UserDataLastModifiedAt = MaxDateTimeOffset(
            existing.UserDataLastModifiedAt,
            localMetadata.UserDataLastModifiedAt);
        existing.LastModifiedAt = MaxDateTimeOffset(existing.LastModifiedAt, local.LastModifiedAt);
    }

    private async Task<CanonicalSalvage?> TryReadCanonicalSalvageAsync(
        User existing,
        EncryptionKey key,
        long expectedKeyEpoch,
        long expectedMembershipEpoch,
        CancellationToken ct)
    {
        if (_checkpoints is null || _identity is null)
            return null;

        var checkpoint = await _checkpoints.GetAsync(existing.UId, ct);
        if (checkpoint is null)
            return null;
        try
        {
            UserCanonicalCheckpointUtil.VerifyAuthenticity(checkpoint, existing.UId, _identity);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or ArgumentException)
        {
            return null;
        }

        if (checkpoint.KeyEpoch != expectedKeyEpoch ||
            checkpoint.MembershipEpoch != expectedMembershipEpoch)
            return null;

        UserData? root = null;
        GeneralUserData? general = null;
        UserPasswordsData? passwords = null;
        UserDevicesData? devices = null;
        try
        {
            root = await DecryptDecompressDeserializeAsync(
                existing.EncryptedPayload,
                key,
                BackendJsonSerializerContext.Default.UserData,
                ct: ct);
            if (root is null)
                return null;
            _integrity.VerifyUserData(root);

            general = await TryReadSalvageBlobAsync(
                existing.EncryptedGeneralUserDataPayload,
                root.GeneralUserDataKey,
                BackendJsonSerializerContext.Default.GeneralUserData,
                _integrity.VerifyGeneralUserData,
                root.GeneralUserDataIntegrityHash,
                ct);
            passwords = await TryReadSalvageBlobAsync(
                existing.EncryptedUserPasswordsDataPayload,
                root.UserPasswordsDataKey,
                BackendJsonSerializerContext.Default.UserPasswordsData,
                _integrity.VerifyUserPasswordsData,
                root.UserPasswordsDataIntegrityHash,
                ct);
            devices = await TryReadSalvageBlobAsync(
                existing.EncryptedUserDevicesDataPayload,
                root.UserDevicesDataKey,
                BackendJsonSerializerContext.Default.UserDevicesData,
                _integrity.VerifyUserDevicesData,
                root.UserDevicesDataIntegrityHash,
                ct);

            // Cleartext login metadata is part of the canonical identity contract. A locally
            // healthy general blob is salvageable only when it still agrees with that metadata.
            if (general is not null)
            {
                try
                {
                    UserLoginIdentityMetadataUtil.Verify(existing, general);
                }
                catch (InvalidDataException)
                {
                    general.Dispose();
                    general = null;
                }
            }

            var failed = UserDataBlobKind.None;
            if (general is null)
                failed |= UserDataBlobKind.General;
            if (passwords is null)
                failed |= UserDataBlobKind.Passwords;
            if (devices is null)
                failed |= UserDataBlobKind.Devices;
            if (failed == UserDataBlobKind.All)
                return null;

            var result = new CanonicalSalvage(
                root,
                general,
                passwords,
                devices,
                failed,
                existing.LastModifiedAt);
            root = null;
            general = null;
            passwords = null;
            devices = null;
            return result;
        }
        catch (Exception ex) when (IsSnapshotVerificationFailure(ex) || ex is ArgumentException)
        {
            return null;
        }
        finally
        {
            root?.Dispose();
            general?.Dispose();
            passwords?.Dispose();
            devices?.Dispose();
        }
    }

    private async Task<T?> TryReadSalvageBlobAsync<T>(
        byte[] encryptedBlob,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Action<T> verifyIntegrity,
        byte[] expectedRootHash,
        CancellationToken ct) where T : class, IDisposable
    {
        try
        {
            var value = await DecryptAndVerifyEncryptedUserBlobAsync(
                encryptedBlob,
                rawKey,
                typeInfo,
                verifyIntegrity,
                ct);
            if (expectedRootHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
                value is not IntegrityCheckableBase integrityValue ||
                !Hashing.Verify(expectedRootHash, integrityValue.IntegrityHash))
            {
                value.Dispose();
                return null;
            }
            return value;
        }
        catch (Exception ex) when (IsSnapshotVerificationFailure(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    private bool HasSameRootKeyMaterial(UserData first, UserData second) =>
        first.FormatVersion == second.FormatVersion &&
        first.UId == second.UId &&
        Hashing.Verify(first.GeneralUserDataKey, second.GeneralUserDataKey) &&
        Hashing.Verify(first.UserPasswordsDataKey, second.UserPasswordsDataKey) &&
        Hashing.Verify(first.UserDevicesDataKey, second.UserDevicesDataKey);

    private UserSyncPayload CreateRecoveryMetadata(User source, GeneralUserData? trustedGeneral)
    {
        var metadata = new UserSyncPayload
        {
            UId = source.UId,
            UsernameHash = source.UsernameHash.ToArray(),
            UsernameSalt = source.UsernameSalt.ToArray(),
            GeneralUserDataVersion = source.GetGeneralUserDataVersion(),
            PasswordSalt = source.PasswordSalt.ToArray(),
            UserDataLastModifiedAt = source.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = source.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = source.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = source.UserDevicesDataLastModifiedAt
        };

        // TryReadCanonicalSalvageAsync already excludes a general component whose authenticated
        // login metadata does not match. Never invent a new salt/hash at the same deterministic
        // version during recovery, because that would create a nondeterministic identity branch.
        if (trustedGeneral is not null)
            UserLoginIdentityMetadataUtil.Verify(metadata.UsernameHash, metadata.UsernameSalt, metadata.GeneralUserDataVersion, trustedGeneral);

        return metadata;
    }


    private RecoveryCandidateVerificationResult CandidateFailure(
        UserSnapshotEnvelope snapshot,
        RecoveryCandidateState state,
        UserDataBlobKind failedComponents,
        string diagnosticCode) =>
        new(
            snapshot.OriginDeviceId,
            snapshot.OriginInstanceId,
            snapshot.OriginRevision,
            snapshot.SnapshotHash.ToArray(),
            state,
            failedComponents,
            diagnosticCode);

    private RecoveryCandidateState MapRecoveryCandidateState(UserDataVerificationState state) => state switch
    {
        UserDataVerificationState.RootDecryptFailure => RecoveryCandidateState.DecryptFailed,
        UserDataVerificationState.RootIntegrityFailure or
        UserDataVerificationState.GeneralBlobFailure or
        UserDataVerificationState.PasswordsBlobFailure or
        UserDataVerificationState.DevicesBlobFailure or
        UserDataVerificationState.BundleLinkFailure => RecoveryCandidateState.IntegrityFailed,
        UserDataVerificationState.LoginMetadataFailure => RecoveryCandidateState.MetadataMismatch,
        UserDataVerificationState.AuthorizationFailure => RecoveryCandidateState.UnauthorizedOrigin,
        UserDataVerificationState.EpochFailure => RecoveryCandidateState.WrongKeyEpoch,
        UserDataVerificationState.Fork => RecoveryCandidateState.Forked,
        _ => RecoveryCandidateState.IntegrityFailed
    };

    private void CopyRecoveryMetadata(UserSyncPayload source, User target)
    {
        CryptographicOperations.ZeroMemory(target.UsernameHash);
        CryptographicOperations.ZeroMemory(target.UsernameSalt);
        CryptographicOperations.ZeroMemory(target.PasswordSalt);
        target.UsernameHash = source.UsernameHash.ToArray();
        target.UsernameSalt = source.UsernameSalt.ToArray();
        target.PasswordSalt = source.PasswordSalt.ToArray();
        target.SetGeneralUserDataVersion(source.GeneralUserDataVersion);
        target.UserDataLastModifiedAt = source.UserDataLastModifiedAt;
        target.GeneralUserDataLastModifiedAt = source.GeneralUserDataLastModifiedAt;
        target.UserPasswordsDataLastModifiedAt = source.UserPasswordsDataLastModifiedAt;
        target.UserDevicesDataLastModifiedAt = source.UserDevicesDataLastModifiedAt;
    }

    private void DisposeVerificationResults(
        IReadOnlyList<UserDataBundleVerificationResult> verificationResults)
    {
        foreach (var verificationResult in verificationResults)
            verificationResult.Dispose();
    }

    private UserSnapshotMergeEntryResult Failed(
        UserSnapshotEnvelope snapshot,
        UserDataBundleVerificationResult verification) =>
        new(
            snapshot.OriginDeviceId,
            snapshot.OriginInstanceId,
            snapshot.OriginRevision,
            false,
            verification.DiagnosticCode,
            verification.State,
            verification.FailedBlobs,
            verification.DiagnosticCode);

    private bool IsSnapshotVerificationFailure(Exception ex) =>
        ex is UnauthorizedAccessException or
            CryptographicException or
            InvalidDataException or
            InvalidDataIntegrityException or
            JsonException;


    private async Task<UserDataBundle> ReadAndVerifyUserDataBundleForSyncAsync(User user, EncryptionKey userKey, CancellationToken ct)
    {
        var userData = await DecryptDecompressDeserializeAsync(user.EncryptedPayload, userKey, BackendJsonSerializerContext.Default.UserData, ct: ct);
        if (userData is null)
            throw new UnauthorizedAccessException();

        try
        {
            _integrity.VerifyUserData(userData);
        }
        catch
        {
            userData.Dispose();
            throw;
        }

        var generalTask = DecryptAndVerifyEncryptedUserBlobAsync(
            user.EncryptedGeneralUserDataPayload,
            userData.GeneralUserDataKey,
            BackendJsonSerializerContext.Default.GeneralUserData,
            _integrity.VerifyGeneralUserData,
            ct);
        var passwordsTask = DecryptAndVerifyEncryptedUserBlobAsync(
            user.EncryptedUserPasswordsDataPayload,
            userData.UserPasswordsDataKey,
            BackendJsonSerializerContext.Default.UserPasswordsData,
            _integrity.VerifyUserPasswordsData,
            ct);
        var devicesTask = DecryptAndVerifyEncryptedUserBlobAsync(
            user.EncryptedUserDevicesDataPayload,
            userData.UserDevicesDataKey,
            BackendJsonSerializerContext.Default.UserDevicesData,
            _integrity.VerifyUserDevicesData,
            ct);

        try
        {
            await Task.WhenAll(generalTask, passwordsTask, devicesTask);

            var bundle = new UserDataBundle
            {
                UserData = userData,
                GeneralUserData = await generalTask,
                UserPasswordsData = await passwordsTask,
                UserDevicesData = await devicesTask
            };

            _integrity.VerifyBundleLinks(bundle);
            return bundle;
        }
        catch
        {
            DisposeCompletedTaskResult(generalTask);
            DisposeCompletedTaskResult(passwordsTask);
            DisposeCompletedTaskResult(devicesTask);
            userData.Dispose();
            throw;
        }
    }


    private async Task<T> DecryptEncryptedUserBlobAsync<T>(byte[] encryptedBlob, byte[] rawKey, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        if (encryptedBlob.Length == 0 || rawKey.Length == 0)
            throw new UnauthorizedAccessException();

        using var key = EncryptionKey.FromRaw(rawKey);
        var data = await DecryptDecompressDeserializeAsync(encryptedBlob, key, typeInfo, ct: ct);
        if (data is null)
            throw new UnauthorizedAccessException();

        return data;
    }


    private async Task<T> DecryptAndVerifyEncryptedUserBlobAsync<T>(
        byte[] encryptedBlob,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Action<T> verifyIntegrity,
        CancellationToken ct) where T : class, IDisposable
    {
        var data = await DecryptEncryptedUserBlobAsync(encryptedBlob, rawKey, typeInfo, ct);
        try
        {
            verifyIntegrity(data);
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }


    private async Task PersistMergedUserBundleAsync(
        User user,
        UserDataBundle bundle,
        EncryptionKey userKey,
        UserDataBlobKind changedBlobs,
        CancellationToken ct)
    {
        _integrity.RebuildModifiedBlobIntegrity(bundle, changedBlobs);

        Task<byte[]>? generalTask = changedBlobs.HasFlag(UserDataBlobKind.General)
            ? EncryptUserBlobAsync(bundle.GeneralUserData, bundle.UserData.GeneralUserDataKey, BackendJsonSerializerContext.Default.GeneralUserData, ct)
            : null;
        Task<byte[]>? passwordsTask = changedBlobs.HasFlag(UserDataBlobKind.Passwords)
            ? EncryptUserBlobAsync(bundle.UserPasswordsData, bundle.UserData.UserPasswordsDataKey, BackendJsonSerializerContext.Default.UserPasswordsData, ct)
            : null;
        Task<byte[]>? devicesTask = changedBlobs.HasFlag(UserDataBlobKind.Devices)
            ? EncryptUserBlobAsync(bundle.UserDevicesData, bundle.UserData.UserDevicesDataKey, BackendJsonSerializerContext.Default.UserDevicesData, ct)
            : null;
        var userDataTask = SerializeCompressEncryptAsync(bundle.UserData, userKey, BackendJsonSerializerContext.Default.UserData, ct: ct);

        var encryptionTasks = new List<Task<byte[]>> { userDataTask };
        if (generalTask is not null)
            encryptionTasks.Add(generalTask);
        if (passwordsTask is not null)
            encryptionTasks.Add(passwordsTask);
        if (devicesTask is not null)
            encryptionTasks.Add(devicesTask);

        try
        {
            await Task.WhenAll(encryptionTasks);
        }
        catch
        {
            foreach (var task in encryptionTasks)
                ZeroCompletedEncryptionTask(task);
            throw;
        }

        if (generalTask is not null)
            ReplaceEncryptedPayload(user.EncryptedGeneralUserDataPayload, await generalTask, value => user.EncryptedGeneralUserDataPayload = value);
        if (passwordsTask is not null)
            ReplaceEncryptedPayload(user.EncryptedUserPasswordsDataPayload, await passwordsTask, value => user.EncryptedUserPasswordsDataPayload = value);
        if (devicesTask is not null)
            ReplaceEncryptedPayload(user.EncryptedUserDevicesDataPayload, await devicesTask, value => user.EncryptedUserDevicesDataPayload = value);

        ReplaceEncryptedPayload(user.EncryptedPayload, await userDataTask, value => user.EncryptedPayload = value);
    }


    private async Task<byte[]> EncryptUserBlobAsync<T>(
        T data,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct) where T : class
    {
        using var key = EncryptionKey.FromRaw(rawKey);
        return await SerializeCompressEncryptAsync(data, key, typeInfo, ct: ct);
    }


    private void DisposeCompletedTaskResult<T>(Task<T> task) where T : IDisposable
    {
        if (task.Status == TaskStatus.RanToCompletion)
            task.Result.Dispose();
    }


    private void ZeroCompletedEncryptionTask(Task<byte[]> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
            CryptographicOperations.ZeroMemory(task.Result);
    }


    private void ReplaceEncryptedPayload(byte[] currentPayload, byte[] replacementPayload, Action<byte[]> assignReplacement)
    {
        // These encrypted payload properties are EF Core concurrency tokens. Their tracked
        // OriginalValue can reference the same byte[] instance as the entity property. Zeroing
        // that array before SaveChanges would therefore corrupt the optimistic-concurrency
        // predicate and produce a false DbUpdateConcurrencyException. Ciphertext is replaced
        // without in-place mutation; decrypted keys and logical bundles retain their existing
        // explicit disposal/zeroization behavior.
        _ = currentPayload;
        assignReplacement(replacementPayload);
    }


    private DateTimeOffset MaxDateTimeOffset(params DateTimeOffset[] values)
    {
        var max = DateTimeOffset.MinValue;
        foreach (var value in values)
        {
            if (value != default && value > max)
                max = value;
        }

        return max;
    }


    private User CreateUser(UserSyncPayload payload) =>
        new()
        {
            UId = payload.UId
        };


    private void CopyUserData(UserSyncPayload source, User target)
    {
        target.UId = source.UId;
        target.UsernameHash = source.UsernameHash;
        target.UsernameSalt = source.UsernameSalt;
        target.SetGeneralUserDataVersion(source.GeneralUserDataVersion);
        target.PasswordSalt = source.PasswordSalt;
        target.EncryptedPayload = source.EncryptedPayload;
        target.EncryptedGeneralUserDataPayload = source.EncryptedGeneralUserDataPayload;
        target.EncryptedUserPasswordsDataPayload = source.EncryptedUserPasswordsDataPayload;
        target.EncryptedUserDevicesDataPayload = source.EncryptedUserDevicesDataPayload;
        target.UserDataLastModifiedAt = source.UserDataLastModifiedAt;
        target.GeneralUserDataLastModifiedAt = source.GeneralUserDataLastModifiedAt;
        target.UserPasswordsDataLastModifiedAt = source.UserPasswordsDataLastModifiedAt;
        target.UserDevicesDataLastModifiedAt = source.UserDevicesDataLastModifiedAt;
        target.IntegrityHash = source.IntegrityHash;
    }


    private DateTimeOffset FromTimestamp(long ts) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ts);
}
