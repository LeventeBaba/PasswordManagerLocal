using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Checks retained membership history and repairs only values proven by this
/// installation's identity or by its applied, signed control operations.</summary>
public sealed class SelfDiagnosticsService : ISelfDiagnosticsService
{
    private readonly IUserLookupService _users;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IDeviceIdentityService _identity;
    private readonly ILocalUserDeviceRepository _localLinks;
    private readonly IUserMembershipAuthorizationRepository _authorizations;
    private readonly IUserDeviceRepository _userDevices;
    private readonly IUserControlOperationRepository _operations;
    private readonly IUserOriginRemovalCutoffRepository _cutoffs;
    private readonly IUserControlStateRepository _states;
    private readonly IUserSyncFaultRepository _syncFaults;
    private readonly IUserRevisionKnowledgeRepository _revisionKnowledge;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserSyncStateRepository _syncStates;
    private readonly IDeviceEnrollmentCommitRepository _enrollmentCommits;
    private readonly IUserCanonicalHealthService _canonicalHealth;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUnitOfWork _uow;

    public SelfDiagnosticsService(
        IUserLookupService users, IUserLifecycleCoordinator lifecycle, IDeviceIdentityService identity,
        ILocalUserDeviceRepository localLinks, IUserMembershipAuthorizationRepository authorizations,
        IUserDeviceRepository userDevices,
        IUserControlOperationRepository operations, IUserOriginRemovalCutoffRepository cutoffs,
        IUserControlStateRepository states, IUserSyncFaultRepository syncFaults,
        IUserRevisionKnowledgeRepository revisionKnowledge,
        IUserSyncSnapshotRepository snapshots, IUserSyncStateRepository syncStates,
        IDeviceEnrollmentCommitRepository enrollmentCommits, IUserCanonicalHealthService canonicalHealth,
        IUserLoginIdentityProjectionService loginIdentities, IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter, ISyncVersionClockService versionClock, IUnitOfWork uow)
    {
        _users = users;
        _lifecycle = lifecycle;
        _identity = identity;
        _localLinks = localLinks;
        _authorizations = authorizations;
        _userDevices = userDevices;
        _operations = operations;
        _cutoffs = cutoffs;
        _states = states;
        _syncFaults = syncFaults;
        _revisionKnowledge = revisionKnowledge;
        _snapshots = snapshots;
        _syncStates = syncStates;
        _enrollmentCommits = enrollmentCommits;
        _canonicalHealth = canonicalHealth;
        _loginIdentities = loginIdentities;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _versionClock = versionClock;
        _uow = uow;
    }

    public async Task<SelfDiagnosticsResultResponse> RunAsync(Guid token, CancellationToken ct = default)
    {
        var userId = (await _users.GetAndVerifyUserAsync(token, ct)).UId;
        return await _lifecycle.ExecuteAsync(userId, async lifecycleToken =>
        {
            var user = await _users.GetAndVerifyUserAsync(token, lifecycleToken);
            var findings = new HashSet<string>();
            var local = await _localLinks.GetAsync(user.UId, lifecycleToken);
            var rows = await _authorizations.ListForUserAsync(user.UId, lifecycleToken);
            var own = rows.Where(row => row.IsActive && row.DeviceId == _identity.LocalDeviceId &&
                                         row.OriginInstanceId == _identity.OriginInstanceId).ToArray();
            var localIdentityMatches = local is not null && local.IsIntegrityValid() &&
                local.LocalDeviceIdentityId == _identity.LocalDeviceId && own.Length == 1 &&
                own[0].SignPublicKey.SequenceEqual(_identity.SignPublicKey) &&
                Hashing.Verify(own[0].AgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) &&
                string.Equals(SyncIdentityUtil.NormalizeFingerprint(own[0].TlsCertFingerprint),
                    SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase) &&
                own[0].DeviceType == _identity.DeviceType && own[0].MinimumKeyEpoch <= user.KeyEpoch &&
                (own[0].MaximumKeyEpoch is not long localMaximum || localMaximum >= user.KeyEpoch);
            // The current installation can independently derive this hash from its own signing key.
            var localSignHashRepair = localIdentityMatches && own[0].IsGenesis &&
                own[0].StartedMembershipEpoch == 1 &&
                !Hashing.Verify(own[0].SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey));
            if (!localIdentityMatches ||
                (!localSignHashRepair && own.Length == 1 &&
                 !Hashing.Verify(own[0].SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey))))
                findings.Add("LocalMembershipInvalid");
            if (rows.Count(row => row.IsGenesis && row.StartedMembershipEpoch == 1) != 1)
                findings.Add("GenesisHistoryInvalid");
            // Several distinct active devices are normal. Only conflicting origins for one device are unsafe.
            if (rows.Where(row => row.IsActive).GroupBy(row => row.DeviceId).Any(group => group.Count() != 1) ||
                rows.GroupBy(row => (row.DeviceId, row.OriginInstanceId)).Any(group => group.Count() != 1) ||
                rows.Select(UserMembershipAuthorizationIdentity.GetCanonicalAuthorizationId).Distinct().Count() != rows.Count)
                findings.Add("MembershipHistoryInvalid");
            var remoteLinks = (await _userDevices.ListByUserWithDevicesAsync(user.UId, lifecycleToken))
                .Where(link => link.DeviceId != _identity.LocalDeviceId).ToArray();
            foreach (var authorization in rows.Where(row => row.IsActive && row.DeviceId != _identity.LocalDeviceId))
            {
                var links = remoteLinks.Where(link => link.DeviceId == authorization.DeviceId).ToArray();
                if (links.Length != 1 || links[0].IsDeleted || !links[0].IsIntegrityValid())
                {
                    findings.Add("DeviceLinkInvalid");
                    continue;
                }
                var device = links[0].Device;
                if (device is null || !device.IsIntegrityValid() ||
                    !device.SignPublicKey.SequenceEqual(authorization.SignPublicKey) ||
                    !Hashing.Verify(authorization.AgreementPublicKeyHash, Hashing.SHA256Hash(device.PublicKey)) ||
                    !string.Equals(SyncIdentityUtil.NormalizeFingerprint(device.TlsCertFingerprint),
                        SyncIdentityUtil.NormalizeFingerprint(authorization.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceType != authorization.DeviceType)
                    findings.Add("DeviceRecordInvalid");
            }
            var state = await _states.GetAsync(user.UId, lifecycleToken);
            if (state is null || state.HasConflict || state.AppliedMembershipEpoch != user.MembershipEpoch ||
                state.AppliedKeyEpoch != user.KeyEpoch)
                findings.Add("ControlStateInvalid");
            var operations = await _operations.ListForUserAsync(user.UId, lifecycleToken);
            if (findings.Contains("MembershipHistoryInvalid"))
            {
                var reset = await TryResetSingleDeviceLegacyHistoryAsync(
                    token, user, localIdentityMatches, rows, remoteLinks, state, operations, findings, lifecycleToken);
                if (reset is not null)
                    return reset;
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };
            }

            var genesisRepairs = rows.Where(row => row.IsGenesis && row.StartedMembershipEpoch == 1 &&
                row.AdditionOperationId is null && row.AdditionOperationHash is { Length: 0 }).ToArray();
            var byId = rows.ToDictionary(UserMembershipAuthorizationIdentity.GetCanonicalAuthorizationId);
            var localOperations = operations.Where(operation =>
                operation.Status != UserControlOperationStatus.Rejected &&
                operation.OriginDeviceId == _identity.LocalDeviceId &&
                operation.OriginInstanceId == _identity.OriginInstanceId).ToArray();
            if (state is not null && (state.NextOriginSequence <= 0 ||
                (localOperations.Length > 0 &&
                 (state.LocalOriginInstanceId != _identity.OriginInstanceId ||
                  state.NextOriginSequence <= localOperations.Max(operation => operation.OriginSequence)))))
                findings.Add("ControlSequenceInvalid");
            var additionRepairs = new Dictionary<Guid, (UserMembershipAuthorization Row, Guid Id, byte[] Hash)>();
            var membershipSignHashRepairs = new List<UserMembershipAuthorization>();
            foreach (var row in rows)
            {
                var hasValidSignHash = Hashing.Verify(row.SignPublicKeyHash, Hashing.SHA256Hash(row.SignPublicKey));
                if (row.IsGenesis)
                {
                    if (!hasValidSignHash && (!localSignHashRepair || row != own[0]))
                        findings.Add("MembershipIdentityInvalid");
                    if (row.StartedMembershipEpoch != 1 || row.AdditionOperationId is not null ||
                        row.AdditionOperationHash is { Length: > 0 })
                        findings.Add("GenesisHistoryInvalid");
                    continue;
                }
                // Only an operation signed by the current installation can restore a broken
                // addition reference without trusting another potentially damaged history row.
                var candidates = new List<UserControlOperationEnvelope>();
                foreach (var operation in operations.Where(item =>
                             item.Status == UserControlOperationStatus.Applied &&
                             item.OperationType == UserControlOperationType.DeviceAddition &&
                             item.ResultingMembershipEpoch == row.StartedMembershipEpoch))
                {
                    if (localIdentityMatches &&
                        TryReadLocalSignedOperation(operation, own[0], out var envelope) &&
                        AdditionMatches(row, envelope!))
                        candidates.Add(envelope!);
                }
                if (candidates.Count == 1)
                {
                    var candidate = candidates[0];
                    if (!hasValidSignHash)
                        membershipSignHashRepairs.Add(row);
                    if (row.AdditionOperationId != candidate.OperationId || row.AdditionOperationHash is null ||
                        !Hashing.Verify(row.AdditionOperationHash, candidate.OperationHash))
                        additionRepairs.Add(row.AuthorizationId, (row, candidate.OperationId, candidate.OperationHash.ToArray()));
                }
                else if (candidates.Count > 1 || row.AdditionOperationId is null ||
                         row.AdditionOperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes })
                    findings.Add("AdditionHistoryInvalid");
                else
                {
                    // Another device may have signed this addition. Verify its immutable
                    // reference and signature, but never derive a replacement from that
                    // device's possibly damaged authorization row.
                    var retained = operations.SingleOrDefault(item => item.OperationId == row.AdditionOperationId);
                    try
                    {
                        if (retained is null || retained.Status != UserControlOperationStatus.Applied ||
                            retained.OperationType != UserControlOperationType.DeviceAddition)
                            throw new InvalidDataException("The signed device addition is missing.");
                        var envelope = UserControlOperationEnvelopeUtil.Deserialize(retained.EnvelopePayload);
                        var authors = rows.Where(item =>
                            item.DeviceId == envelope.OriginDeviceId &&
                            item.OriginInstanceId == envelope.OriginInstanceId &&
                            item.StartedMembershipEpoch <= envelope.PreviousMembershipEpoch &&
                            (item.EndedMembershipEpoch is null ||
                             item.EndedMembershipEpoch > envelope.PreviousMembershipEpoch)).Take(2).ToArray();
                        if (!OperationMatches(retained, envelope) || authors.Length != 1 ||
                            !Hashing.Verify(authors[0].SignPublicKeyHash,
                                Hashing.SHA256Hash(authors[0].SignPublicKey)) ||
                            envelope.PreviousKeyEpoch < authors[0].MinimumKeyEpoch ||
                            (authors[0].MaximumKeyEpoch is long maximum && envelope.PreviousKeyEpoch > maximum) ||
                            !AdditionMatches(row, envelope) ||
                            !Hashing.Verify(row.AdditionOperationHash, envelope.OperationHash))
                            throw new InvalidDataException("The signed device addition does not match its authorization.");
                        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, authors[0].SignPublicKey);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException)
                    {
                        findings.Add("AdditionHistoryInvalid");
                        BackendDebugLog.Warning($"Self diagnostics found invalid addition history. " +
                            $"UserId={user.UId}, AuthorizationId={row.AuthorizationId}.", ex, "SelfDiagnostics");
                    }
                }
                if (!hasValidSignHash && candidates.Count != 1)
                    findings.Add("MembershipIdentityInvalid");
            }

            var existingCutoffs = await _cutoffs.ListForUserAsync(user.UId, lifecycleToken);
            var cutoffByNamespace = existingCutoffs.GroupBy(row => (row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch))
                .ToDictionary(group => group.Key, group => group.ToArray());
            if (cutoffByNamespace.Any(entry => entry.Value.Length != 1))
                findings.Add("RemovalCutoffInvalid");
            var expectedCutoffs = new Dictionary<(Guid DeviceId, Guid OriginId, long KeyEpoch), UserOriginRemovalCutoff>();
            var missingCutoffs = new List<UserOriginRemovalCutoff>();
            foreach (var operation in operations.Where(item =>
                         item.Status == UserControlOperationStatus.Applied &&
                         item.OperationType == UserControlOperationType.DeviceRemoval))
            {
                try
                {
                    var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
                    var author = await _authorizations.GetForSignedEpochAsync(
                        user.UId, envelope.OriginDeviceId, envelope.OriginInstanceId,
                        envelope.PreviousMembershipEpoch, lifecycleToken);
                    if (!OperationMatches(operation, envelope) || author is null ||
                        envelope.PreviousKeyEpoch < author.MinimumKeyEpoch ||
                        (author.MaximumKeyEpoch is long maximum && envelope.PreviousKeyEpoch > maximum))
                        throw new InvalidDataException("The retained removal operation or signer is inconsistent.");
                    UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, author.SignPublicKey);
                    var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
                    if (payload.UserId != user.UId || payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                        payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
                        payload.KeyEpoch != envelope.PreviousKeyEpoch)
                        throw new InvalidDataException("The retained removal payload header is inconsistent.");
                    foreach (var origin in payload.Origins)
                    {
                        if (!byId.TryGetValue(origin.AuthorizationId, out var target) ||
                            target.DeviceId != payload.RemovedDeviceId ||
                            target.OriginInstanceId != origin.OriginInstanceId ||
                            !Hashing.Verify((localSignHashRepair && target == own[0]) ||
                                membershipSignHashRepairs.Contains(target)
                                ? Hashing.SHA256Hash(target.SignPublicKey) : target.SignPublicKeyHash,
                                origin.SignPublicKeyHash) ||
                            target.IsActive || target.EndedMembershipEpoch != payload.ResultingMembershipEpoch ||
                            target.RemovalOperationId != envelope.OperationId || target.RemovalOperationHash is null ||
                            !Hashing.Verify(target.RemovalOperationHash, envelope.OperationHash) ||
                            origin.UserKeyEpoch < target.MinimumKeyEpoch ||
                            (target.MaximumKeyEpoch is long targetMaximum && origin.UserKeyEpoch > targetMaximum) ||
                            (target.IsGenesis
                                ? target.StartedMembershipEpoch != 1 || origin.AdditionOperationId is not null ||
                                  origin.AdditionOperationHash is not null
                                : !MatchesAdditionEvidence(target, origin, additionRepairs)))
                            throw new InvalidDataException("The retained removal cutoff conflicts with its membership authorization.");
                        var key = (payload.RemovedDeviceId, origin.OriginInstanceId, origin.UserKeyEpoch);
                        var expected = new UserOriginRemovalCutoff
                        {
                            UserId = user.UId, DeviceId = key.RemovedDeviceId,
                            OriginInstanceId = key.OriginInstanceId, UserKeyEpoch = key.UserKeyEpoch,
                            HighestAcceptedSnapshotRevision = origin.HighestAcceptedSnapshotRevision,
                            HighestAcceptedControlSequence = origin.HighestAcceptedControlSequence,
                            ResultingMembershipEpoch = payload.ResultingMembershipEpoch,
                            AuthorizationId = origin.AuthorizationId, RemovalOperationId = envelope.OperationId,
                            RemovalOperationHash = envelope.OperationHash.ToArray(), CreatedAtUtc = envelope.CreatedAtUtc
                        };
                        if (!expectedCutoffs.TryAdd(key, expected))
                            throw new InvalidDataException("Multiple signed removals claim the same origin key namespace.");
                        if (cutoffByNamespace.TryGetValue(key, out var retained))
                        {
                            if (retained.Length != 1 || !CutoffMatches(retained[0], expected))
                                throw new InvalidDataException("A stored removal cutoff disagrees with the signed operation.");
                        }
                        else if (localIdentityMatches &&
                                 author.AuthorizationId == own[0].AuthorizationId &&
                                 IsSignedByThisInstallation(envelope))
                            missingCutoffs.Add(expected);
                        else
                            findings.Add("RemovalCutoffMissing");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    findings.Add("SignedRemovalHistoryInvalid");
                    BackendDebugLog.Warning($"Self diagnostics found invalid signed removal history. " +
                        $"UserId={user.UId}, OperationId={operation.OperationId}.", ex, "SelfDiagnostics");
                }
            }
            if (existingCutoffs.Any(row => !expectedCutoffs.ContainsKey((row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch))))
                findings.Add("RemovalCutoffInvalid");
            if (findings.Count != 0)
            {
                var reset = await TryResetSingleDeviceLegacyHistoryAsync(
                    token, user, localIdentityMatches, rows, remoteLinks, state, operations, findings, lifecycleToken);
                if (reset is not null)
                    return reset;
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };
            }

            var repairCount = genesisRepairs.Length + additionRepairs.Count + missingCutoffs.Count +
                              membershipSignHashRepairs.Count +
                              (localSignHashRepair ? 1 : 0);
            if (repairCount > 0)
            {
                await using var transaction = await _uow.BeginTransactionAsync(lifecycleToken);
                try
                {
                    foreach (var row in genesisRepairs)
                    {
                        row.AdditionOperationHash = null;
                        row.Version = checked(row.Version + 1);
                        _authorizations.Update(row);
                    }
                    foreach (var repair in additionRepairs.Values)
                    {
                        repair.Row.AdditionOperationId = repair.Id;
                        repair.Row.AdditionOperationHash = repair.Hash;
                        repair.Row.Version = checked(repair.Row.Version + 1);
                        _authorizations.Update(repair.Row);
                    }
                    foreach (var row in membershipSignHashRepairs)
                    {
                        row.SignPublicKeyHash = Hashing.SHA256Hash(row.SignPublicKey);
                        if (!additionRepairs.ContainsKey(row.AuthorizationId))
                            row.Version = checked(row.Version + 1);
                        _authorizations.Update(row);
                    }
                    if (localSignHashRepair)
                    {
                        own[0].SignPublicKeyHash = Hashing.SHA256Hash(_identity.SignPublicKey);
                        own[0].Version = checked(own[0].Version + 1);
                        _authorizations.Update(own[0]);
                    }
                    foreach (var cutoff in missingCutoffs)
                        await _cutoffs.AddAsync(cutoff, lifecycleToken);
                    await _uow.SaveChangesAsync(lifecycleToken);
                    await transaction.CommitAsync(lifecycleToken);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    _uow.ClearTrackedChanges();
                    throw;
                }
                BackendDebugLog.Info($"Repaired verified membership evidence. " +
                    $"UserId={user.UId}, Genesis={genesisRepairs.Length}, Additions={additionRepairs.Count}, " +
                    $"Cutoffs={missingCutoffs.Count}, MembershipSignHashes={membershipSignHashRepairs.Count}, " +
                    $"LocalSignHash={(localSignHashRepair ? 1 : 0)}.", "SelfDiagnostics");
            }
            var repaired = new List<string>();
            if (genesisRepairs.Length != 0) repaired.Add("GenesisHashNormalized");
            if (additionRepairs.Count != 0) repaired.Add("AdditionEvidenceRecovered");
            if (missingCutoffs.Count != 0) repaired.Add("RemovalCutoffRecovered");
            if (localSignHashRepair) repaired.Add("LocalSignHashRecovered");
            if (membershipSignHashRepairs.Count != 0) repaired.Add("MembershipSignHashRecovered");
            var metadataRepairCount = await RepairRemovedDeviceMetadataAsync(
                token, user.UId, localIdentityMatches, lifecycleToken);
            if (metadataRepairCount != 0)
            {
                repairCount += metadataRepairCount;
                repaired.Add("RemovedDeviceMetadataCleaned");
            }
            return new SelfDiagnosticsResultResponse { Healthy = true, RepairedCount = repairCount, Findings = repaired };
        }, ct);
    }

    private async Task<SelfDiagnosticsResultResponse?> TryResetSingleDeviceLegacyHistoryAsync(
        Guid token,
        User user,
        bool localIdentityMatches,
        IReadOnlyList<UserMembershipAuthorization> rows,
        IReadOnlyList<UserDevice> remoteLinks,
        UserControlState? state,
        IReadOnlyList<UserControlOperation> operations,
        IReadOnlyCollection<string> findings,
        CancellationToken ct)
    {
        if (findings.Count == 0 || !localIdentityMatches || user.KeyEpoch != 1)
            return null;

        var resettableFindings = new HashSet<string>(StringComparer.Ordinal)
        {
            "LocalMembershipInvalid",
            "GenesisHistoryInvalid",
            "MembershipHistoryInvalid",
            "ControlStateInvalid",
            "ControlSequenceInvalid",
            "AdditionHistoryInvalid",
            "MembershipIdentityInvalid",
            "RemovalCutoffMissing",
            "RemovalCutoffInvalid",
            "SignedRemovalHistoryInvalid"
        };
        if (findings.Any(finding => !resettableFindings.Contains(finding)))
            return null;
        var hasLegacyHistoryEvidence = findings.Any(finding => finding is
            "LocalMembershipInvalid" or "GenesisHistoryInvalid" or "MembershipHistoryInvalid" or
            "AdditionHistoryInvalid" or "MembershipIdentityInvalid" or "RemovalCutoffMissing" or
            "RemovalCutoffInvalid" or "SignedRemovalHistoryInvalid");
        if (!hasLegacyHistoryEvidence)
            return null;

        var active = rows.Where(row => row.IsActive).ToArray();
        if (active.Length != 1 || active[0].DeviceId != _identity.LocalDeviceId ||
            active[0].OriginInstanceId != _identity.OriginInstanceId ||
            operations.Any(operation => operation.OperationType == UserControlOperationType.AccountDeletion &&
                                        operation.Status != UserControlOperationStatus.Rejected))
            return null;

        var current = active[0];
        var staleAuthorizations = rows.Where(row => row.AuthorizationId != current.AuthorizationId).ToArray();
        var cutoffs = await _cutoffs.ListForUserAsync(user.UId, ct);
        var revisionKnowledge = await _revisionKnowledge.ListForUserAsync(user.UId, ct);
        var snapshots = await _snapshots.ListForUserAsync(user.UId, ct);
        var enrollmentCommits = await _enrollmentCommits.ListForUserAsync(user.UId, ct);
        var syncFaults = await _syncFaults.ListForUserAsync(user.UId, ct);
        var obsoleteSyncFaults = syncFaults.Where(fault =>
            fault.Scope is UserSyncFaultScope.ControlPlane or UserSyncFaultScope.SnapshotOrigin or
                UserSyncFaultScope.SnapshotFork).ToArray();
        var syncState = await _syncStates.GetAsync(user.UId, ct);
        var now = DateTimeOffset.UtcNow;
        using var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        var staleEncryptedDevices = bundle.UserDevicesData.Devices
            .Where(device => device.Id != _identity.LocalDeviceId)
            .ToArray();
        var staleEncryptedTombstones = bundle.UserDevicesData.DeletedDevices.ToArray();

        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            current.SignPublicKey = _identity.SignPublicKey.ToArray();
            current.SignPublicKeyHash = Hashing.SHA256Hash(_identity.SignPublicKey);
            current.AgreementPublicKeyHash = Hashing.SHA256Hash(_identity.AgreementPublicKey);
            current.TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex);
            current.DeviceType = _identity.DeviceType;
            current.StartedMembershipEpoch = 1;
            current.EndedMembershipEpoch = null;
            current.MinimumKeyEpoch = 1;
            current.MaximumKeyEpoch = null;
            current.IsActive = true;
            current.IsGenesis = true;
            current.AdditionOperationId = null;
            current.AdditionOperationHash = null;
            current.RemovalOperationId = null;
            current.RemovalOperationHash = null;
            current.EndedAtUtc = null;
            if (current.CreatedAtUtc == default)
                current.CreatedAtUtc = _identity.CreatedAt;
            current.Version = checked(current.Version + 1);
            _authorizations.Update(current);

            _authorizations.DeleteRange(staleAuthorizations);
            _cutoffs.DeleteRange(cutoffs);
            _operations.DeleteRange(operations);
            _revisionKnowledge.DeleteRange(revisionKnowledge);
            _snapshots.DeleteRange(snapshots);
            _enrollmentCommits.DeleteRange(enrollmentCommits);
            _syncFaults.DeleteRange(obsoleteSyncFaults);
            foreach (var link in remoteLinks)
                _userDevices.Delete(link);

            foreach (var device in staleEncryptedDevices)
            {
                device.Dispose();
                bundle.UserDevicesData.Devices.Remove(device);
            }
            foreach (var tombstone in staleEncryptedTombstones)
                tombstone.Dispose();
            bundle.UserDevicesData.DeletedDevices.Clear();
            if (staleEncryptedDevices.Length != 0 || staleEncryptedTombstones.Length != 0)
                await _userDataWriter.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false, ct);

            user.MembershipEpoch = 1;
            user.GenerateIntegrityHash();

            var stateIsNew = state is null;
            state ??= new UserControlState { UserId = user.UId };
            if (stateIsNew)
                await _states.AddAsync(state, ct);
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = 1;
            state.AppliedKeyEpoch = user.KeyEpoch;
            state.AppliedMembershipEpoch = 1;
            state.HasConflict = false;
            state.ConflictReason = null;
            state.ConflictingOperationId = null;
            state.ConflictingOperationHash = null;
            state.LastUpdatedAtUtc = now;
            if (!stateIsNew)
                _states.Update(state);

            var syncStateIsNew = syncState is null;
            syncState ??= new UserSyncState
            {
                UserId = user.UId,
                NextOriginRevision = 1
            };
            syncState.LocalOriginInstanceId = _identity.OriginInstanceId;
            syncState.NextOriginRevision = Math.Max(1, syncState.NextOriginRevision);
            syncState.LastPublishedContentHash = [];
            syncState.LastUpdatedAtUtc = now;
            if (syncStateIsNew)
                await _syncStates.AddAsync(syncState, ct);
            else
                _syncStates.Update(syncState);

            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }

        BackendDebugLog.Warning(
            $"Reset legacy membership/control history for a verified single-device account. " +
            $"UserId={user.UId}, Findings={string.Join(',', findings)}, " +
            $"RemovedAuthorizations={staleAuthorizations.Length}, RemovedOperations={operations.Count}, " +
            $"RemovedCutoffs={cutoffs.Count}, RemovedSnapshots={snapshots.Count}, " +
            $"RemovedRevisionKnowledge={revisionKnowledge.Count}, RemovedEnrollmentCommits={enrollmentCommits.Count}, " +
            $"RemovedObsoleteSyncFaults={obsoleteSyncFaults.Length}, " +
            $"RemovedEncryptedDeviceMetadata={staleEncryptedDevices.Length}, " +
            $"RemovedEncryptedDeviceTombstones={staleEncryptedTombstones.Length}.",
            category: "SelfDiagnostics");

        return new SelfDiagnosticsResultResponse
        {
            Healthy = true,
            RepairedCount = 1,
            Findings = ["SingleDeviceLegacyHistoryReset"]
        };
    }

    private async Task<int> RepairRemovedDeviceMetadataAsync(
        Guid token, Guid userId, bool localIdentityMatches, CancellationToken ct)
    {
        var authorizations = await _authorizations.ListForUserAsync(userId, ct);
        var links = await _userDevices.ListByUserAsync(userId, ct);
        var activeAuthorizations = authorizations.Where(row => row.IsActive).ToArray();
        var activeDeviceIds = activeAuthorizations.Select(row => row.DeviceId).ToHashSet();
        activeDeviceIds.Add(_identity.LocalDeviceId);
        var verifiedSingleDeviceState = localIdentityMatches && activeAuthorizations.Length == 1 &&
            activeAuthorizations[0].DeviceId == _identity.LocalDeviceId &&
            activeAuthorizations[0].OriginInstanceId == _identity.OriginInstanceId &&
            !links.Any(link => link.DeviceId != _identity.LocalDeviceId && !link.IsDeleted);

        using var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        var staleDevices = bundle.UserDevicesData.Devices
            .Where(device => !activeDeviceIds.Contains(device.Id) &&
                             (verifiedSingleDeviceState || HasVerifiedRemovalEvidence(
                                 device.Id, authorizations, links, bundle.UserDevicesData)))
            .ToArray();
        if (staleDevices.Length == 0)
            return 0;

        var staleDeviceIds = staleDevices.Select(device => device.Id).ToHashSet();
        _versionClock.Observe(staleDevices.Select(device => device.Version)
            .Concat(bundle.UserDevicesData.DeletedDevices
                .Where(deleted => staleDeviceIds.Contains(deleted.Id))
                .Select(deleted => deleted.Version)));

        var repairs = staleDevices
            .Select(device => new
            {
                Device = device,
                DeletedAt = ResolveRemovalTime(device.Id, authorizations, links, bundle.UserDevicesData),
                Version = _versionClock.Next()
            })
            .ToArray();

        foreach (var repair in repairs)
        {
            TombstoneCleanupUtil.AddOrUpdateDeletedUserDevice(
                bundle.UserDevicesData, repair.Device.Id, repair.DeletedAt, repair.Version);
            repair.Device.Dispose();
            bundle.UserDevicesData.Devices.Remove(repair.Device);
        }

        await _userDataWriter.UpdateUserDataBundleAsync(
            bundle, token, UserDataBlobKind.Devices, true, ct);
        BackendDebugLog.Info(
            $"Removed stale encrypted metadata for {repairs.Length} previously disconnected device(s). UserId={userId}.",
            "SelfDiagnostics");
        return repairs.Length;
    }

    private static bool HasVerifiedRemovalEvidence(
        Guid deviceId,
        IReadOnlyList<UserMembershipAuthorization> authorizations,
        IReadOnlyList<UserDevice> links,
        UserDevicesData userDevicesData)
    {
        if (userDevicesData.DeletedDevices.Any(deleted => deleted.Id == deviceId))
            return true;
        if (authorizations.Any(row => row.DeviceId == deviceId && !row.IsActive &&
                                      (row.EndedMembershipEpoch.HasValue || row.RemovalOperationId.HasValue ||
                                       row.EndedAtUtc.HasValue)))
            return true;
        return links.Any(link => link.DeviceId == deviceId && link.IsDeleted && link.IsIntegrityValid());
    }

    private static DateTimeOffset ResolveRemovalTime(
        Guid deviceId,
        IReadOnlyList<UserMembershipAuthorization> authorizations,
        IReadOnlyList<UserDevice> links,
        UserDevicesData userDevicesData)
    {
        var candidates = authorizations
            .Where(row => row.DeviceId == deviceId && !row.IsActive && row.EndedAtUtc.HasValue)
            .Select(row => row.EndedAtUtc!.Value)
            .Concat(links.Where(link => link.DeviceId == deviceId && link.IsDeleted && link.DeletedAt.HasValue &&
                                        link.IsIntegrityValid())
                .Select(link => link.DeletedAt!.Value))
            .Concat(userDevicesData.DeletedDevices.Where(deleted => deleted.Id == deviceId)
                .Select(deleted => deleted.DeletedAt))
            .ToArray();
        return candidates.Length == 0 ? DateTimeOffset.UtcNow : candidates.Max();
    }

    private bool IsSignedByThisInstallation(UserControlOperationEnvelope envelope) =>
        envelope.OriginDeviceId == _identity.LocalDeviceId &&
        envelope.OriginInstanceId == _identity.OriginInstanceId &&
        envelope.OriginSignPublicKey.SequenceEqual(_identity.SignPublicKey);

    private bool TryReadLocalSignedOperation(UserControlOperation operation,
        UserMembershipAuthorization author, out UserControlOperationEnvelope? envelope)
    {
        envelope = null;
        try
        {
            var candidate = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
            if (!OperationMatches(operation, candidate) || !IsSignedByThisInstallation(candidate) ||
                candidate.PreviousMembershipEpoch < author.StartedMembershipEpoch ||
                (author.EndedMembershipEpoch is long ended && candidate.PreviousMembershipEpoch >= ended) ||
                candidate.PreviousKeyEpoch < author.MinimumKeyEpoch ||
                (author.MaximumKeyEpoch is long maximum && candidate.PreviousKeyEpoch > maximum))
                return false;
            UserControlOperationEnvelopeUtil.VerifyWithSigningKey(candidate, _identity.SignPublicKey);
            envelope = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            BackendDebugLog.Warning($"Cannot verify addition evidence. OperationId={operation.OperationId}.",
                ex, "SelfDiagnostics");
            return false;
        }
    }

    private static bool OperationMatches(UserControlOperation operation, UserControlOperationEnvelope envelope) =>
        operation.OperationId == envelope.OperationId && operation.UserId == envelope.UserId &&
        operation.OperationType == envelope.OperationType &&
        operation.OriginDeviceId == envelope.OriginDeviceId &&
        operation.OriginInstanceId == envelope.OriginInstanceId &&
        operation.OriginSequence == envelope.OriginSequence &&
        operation.PreviousKeyEpoch == envelope.PreviousKeyEpoch &&
        operation.ResultingKeyEpoch == envelope.ResultingKeyEpoch &&
        operation.PreviousMembershipEpoch == envelope.PreviousMembershipEpoch &&
        operation.ResultingMembershipEpoch == envelope.ResultingMembershipEpoch &&
        Hashing.Verify(operation.PayloadHash, envelope.PayloadHash) &&
        Hashing.Verify(operation.OperationHash, envelope.OperationHash) &&
        operation.OriginSignPublicKey.SequenceEqual(envelope.OriginSignPublicKey) &&
        operation.OriginSignature.SequenceEqual(envelope.OriginSignature);

    private static bool AdditionMatches(UserMembershipAuthorization row, UserControlOperationEnvelope envelope)
    {
        try
        {
            var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(envelope.OperationPayload);
            return payload.UserId == row.UserId && payload.NewDeviceId == row.DeviceId &&
                payload.NewOriginInstanceId == row.OriginInstanceId &&
                payload.ResultingMembershipEpoch == row.StartedMembershipEpoch &&
                payload.PreviousMembershipEpoch == envelope.PreviousMembershipEpoch &&
                payload.KeyEpoch == row.MinimumKeyEpoch && payload.KeyEpoch == envelope.PreviousKeyEpoch &&
                envelope.ResultingKeyEpoch == envelope.PreviousKeyEpoch &&
                payload.SignPublicKey.SequenceEqual(row.SignPublicKey) &&
                Hashing.Verify(row.AgreementPublicKeyHash, Hashing.SHA256Hash(payload.AgreementPublicKey)) &&
                string.Equals(SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint),
                    SyncIdentityUtil.NormalizeFingerprint(row.TlsCertFingerprint), StringComparison.Ordinal) &&
                payload.DeviceType == row.DeviceType;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool MatchesAdditionEvidence(
        UserMembershipAuthorization target, DeviceRemovalOriginCutoffPayload origin,
        IReadOnlyDictionary<Guid, (UserMembershipAuthorization Row, Guid Id, byte[] Hash)> repairs)
    {
        var recovered = repairs.TryGetValue(target.AuthorizationId, out var repair);
        var additionId = recovered ? repair.Id : target.AdditionOperationId;
        var additionHash = recovered ? repair.Hash : target.AdditionOperationHash;
        return additionId is not null && additionHash is not null &&
            origin.AdditionOperationId == additionId && origin.AdditionOperationHash is not null &&
            Hashing.Verify(origin.AdditionOperationHash, additionHash);
    }

    private static bool CutoffMatches(UserOriginRemovalCutoff actual, UserOriginRemovalCutoff expected) =>
        actual.UserId == expected.UserId && actual.AuthorizationId == expected.AuthorizationId &&
        actual.ResultingMembershipEpoch == expected.ResultingMembershipEpoch &&
        actual.RemovalOperationId == expected.RemovalOperationId &&
        actual.HighestAcceptedSnapshotRevision == expected.HighestAcceptedSnapshotRevision &&
        actual.HighestAcceptedControlSequence == expected.HighestAcceptedControlSequence &&
        Hashing.Verify(actual.RemovalOperationHash, expected.RemovalOperationHash);
}
