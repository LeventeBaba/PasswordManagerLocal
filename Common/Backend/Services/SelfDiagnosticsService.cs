using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
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
    private readonly IUnitOfWork _uow;

    public SelfDiagnosticsService(
        IUserLookupService users, IUserLifecycleCoordinator lifecycle, IDeviceIdentityService identity,
        ILocalUserDeviceRepository localLinks, IUserMembershipAuthorizationRepository authorizations,
        IUserDeviceRepository userDevices,
        IUserControlOperationRepository operations, IUserOriginRemovalCutoffRepository cutoffs,
        IUserControlStateRepository states, IUnitOfWork uow)
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
                rows.Select(row => row.AuthorizationId).Distinct().Count() != rows.Count)
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
            if (findings.Contains("MembershipHistoryInvalid"))
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };

            var genesisRepairs = rows.Where(row => row.IsGenesis && row.StartedMembershipEpoch == 1 &&
                row.AdditionOperationId is null && row.AdditionOperationHash is { Length: 0 }).ToArray();
            var byId = rows.ToDictionary(row => row.AuthorizationId);
            var operations = await _operations.ListForUserAsync(user.UId, lifecycleToken);
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
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };

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
            return new SelfDiagnosticsResultResponse { Healthy = true, RepairedCount = repairCount, Findings = repaired };
        }, ct);
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
