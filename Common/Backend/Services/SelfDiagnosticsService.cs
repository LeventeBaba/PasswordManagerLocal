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

/// <summary>Checks retained membership history and repairs stale encrypted device metadata only
/// when current signed or tombstoned state proves that the device was removed.</summary>
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
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly ISyncVersionClockService _versionClock;

    public SelfDiagnosticsService(
        IUserLookupService users,
        IUserLifecycleCoordinator lifecycle,
        IDeviceIdentityService identity,
        ILocalUserDeviceRepository localLinks,
        IUserMembershipAuthorizationRepository authorizations,
        IUserDeviceRepository userDevices,
        IUserControlOperationRepository operations,
        IUserOriginRemovalCutoffRepository cutoffs,
        IUserControlStateRepository states,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        ISyncVersionClockService versionClock)
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
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _versionClock = versionClock;
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
                Hashing.Verify(own[0].SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey)) &&
                Hashing.Verify(own[0].AgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) &&
                string.Equals(SyncIdentityUtil.NormalizeFingerprint(own[0].TlsCertFingerprint),
                    SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase) &&
                own[0].DeviceType == _identity.DeviceType && own[0].MinimumKeyEpoch <= user.KeyEpoch &&
                (own[0].MaximumKeyEpoch is not long localMaximum || localMaximum >= user.KeyEpoch);
            if (!localIdentityMatches)
                findings.Add("LocalMembershipInvalid");

            if (rows.Count(row => row.IsGenesis && row.StartedMembershipEpoch == 1) != 1)
                findings.Add("GenesisHistoryInvalid");

            if (rows.Where(row => row.IsActive).GroupBy(row => row.DeviceId).Any(group => group.Count() != 1) ||
                rows.GroupBy(row => (row.DeviceId, row.OriginInstanceId)).Any(group => group.Count() != 1) ||
                rows.Any(row => row.AuthorizationId == Guid.Empty) ||
                rows.Select(row => row.AuthorizationId).Distinct().Count() != rows.Count)
            {
                findings.Add("MembershipHistoryInvalid");
            }

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
                {
                    findings.Add("DeviceRecordInvalid");
                }
            }

            var state = await _states.GetAsync(user.UId, lifecycleToken);
            if (state is null || state.HasConflict || state.AppliedMembershipEpoch != user.MembershipEpoch ||
                state.AppliedKeyEpoch != user.KeyEpoch)
            {
                findings.Add("ControlStateInvalid");
            }

            var operations = await _operations.ListForUserAsync(user.UId, lifecycleToken);
            var localOperations = operations.Where(operation =>
                operation.Status != UserControlOperationStatus.Rejected &&
                operation.OriginDeviceId == _identity.LocalDeviceId &&
                operation.OriginInstanceId == _identity.OriginInstanceId).ToArray();
            if (state is not null && (state.NextOriginSequence <= 0 ||
                (localOperations.Length > 0 &&
                 (state.LocalOriginInstanceId != _identity.OriginInstanceId ||
                  state.NextOriginSequence <= localOperations.Max(operation => operation.OriginSequence)))))
            {
                findings.Add("ControlSequenceInvalid");
            }

            if (findings.Contains("MembershipHistoryInvalid"))
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };

            var byId = rows.ToDictionary(row => row.AuthorizationId);
            foreach (var row in rows)
            {
                if (!Hashing.Verify(row.SignPublicKeyHash, Hashing.SHA256Hash(row.SignPublicKey)))
                    findings.Add("MembershipIdentityInvalid");

                if (row.IsGenesis)
                {
                    if (row.StartedMembershipEpoch != 1 || row.AdditionOperationId is not null ||
                        row.AdditionOperationHash is not null)
                    {
                        findings.Add("GenesisHistoryInvalid");
                    }
                    continue;
                }

                if (row.AdditionOperationId is not Guid additionOperationId || additionOperationId != row.AuthorizationId ||
                    row.AdditionOperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes })
                {
                    findings.Add("AdditionHistoryInvalid");
                    continue;
                }

                var retained = operations.Where(item => item.OperationId == additionOperationId).Take(2).ToArray();
                try
                {
                    if (retained.Length != 1 || retained[0].Status != UserControlOperationStatus.Applied ||
                        retained[0].OperationType != UserControlOperationType.DeviceAddition)
                    {
                        throw new InvalidDataException("The signed device addition is missing.");
                    }

                    var envelope = UserControlOperationEnvelopeUtil.Deserialize(retained[0].EnvelopePayload);
                    var authors = rows.Where(item =>
                        item.DeviceId == envelope.OriginDeviceId &&
                        item.OriginInstanceId == envelope.OriginInstanceId &&
                        item.StartedMembershipEpoch <= envelope.PreviousMembershipEpoch &&
                        (item.EndedMembershipEpoch is null || item.EndedMembershipEpoch > envelope.PreviousMembershipEpoch))
                        .Take(2).ToArray();
                    if (!OperationMatches(retained[0], envelope) || authors.Length != 1 ||
                        !Hashing.Verify(authors[0].SignPublicKeyHash, Hashing.SHA256Hash(authors[0].SignPublicKey)) ||
                        envelope.PreviousKeyEpoch < authors[0].MinimumKeyEpoch ||
                        (authors[0].MaximumKeyEpoch is long maximum && envelope.PreviousKeyEpoch > maximum) ||
                        !AdditionMatches(row, envelope) ||
                        !Hashing.Verify(row.AdditionOperationHash, envelope.OperationHash))
                    {
                        throw new InvalidDataException("The signed device addition does not match its authorization.");
                    }

                    UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, authors[0].SignPublicKey);
                }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    findings.Add("AdditionHistoryInvalid");
                    BackendDebugLog.Warning(
                        $"Self diagnostics found invalid addition history. UserId={user.UId}, AuthorizationId={row.AuthorizationId}.",
                        ex,
                        "SelfDiagnostics");
                }
            }

            var existingCutoffs = await _cutoffs.ListForUserAsync(user.UId, lifecycleToken);
            var cutoffByNamespace = existingCutoffs
                .GroupBy(row => (row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch))
                .ToDictionary(group => group.Key, group => group.ToArray());
            if (cutoffByNamespace.Any(entry => entry.Value.Length != 1))
                findings.Add("RemovalCutoffInvalid");

            var expectedCutoffs = new Dictionary<(Guid DeviceId, Guid OriginId, long KeyEpoch), UserOriginRemovalCutoff>();
            foreach (var operation in operations.Where(item =>
                         item.Status == UserControlOperationStatus.Applied &&
                         item.OperationType == UserControlOperationType.DeviceRemoval))
            {
                try
                {
                    var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
                    var author = await _authorizations.GetForSignedEpochAsync(
                        user.UId,
                        envelope.OriginDeviceId,
                        envelope.OriginInstanceId,
                        envelope.PreviousMembershipEpoch,
                        lifecycleToken);
                    if (!OperationMatches(operation, envelope) || author is null ||
                        envelope.PreviousKeyEpoch < author.MinimumKeyEpoch ||
                        (author.MaximumKeyEpoch is long maximum && envelope.PreviousKeyEpoch > maximum))
                    {
                        throw new InvalidDataException("The retained removal operation or signer is inconsistent.");
                    }

                    UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, author.SignPublicKey);
                    var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
                    if (payload.UserId != user.UId || payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                        payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
                        payload.KeyEpoch != envelope.PreviousKeyEpoch)
                    {
                        throw new InvalidDataException("The retained removal payload header is inconsistent.");
                    }

                    foreach (var origin in payload.Origins)
                    {
                        if (!byId.TryGetValue(origin.AuthorizationId, out var target) ||
                            target.DeviceId != payload.RemovedDeviceId ||
                            target.OriginInstanceId != origin.OriginInstanceId ||
                            !Hashing.Verify(target.SignPublicKeyHash, origin.SignPublicKeyHash) ||
                            target.IsActive || target.EndedMembershipEpoch != payload.ResultingMembershipEpoch ||
                            target.RemovalOperationId != envelope.OperationId || target.RemovalOperationHash is null ||
                            !Hashing.Verify(target.RemovalOperationHash, envelope.OperationHash) ||
                            origin.UserKeyEpoch < target.MinimumKeyEpoch ||
                            (target.MaximumKeyEpoch is long targetMaximum && origin.UserKeyEpoch > targetMaximum) ||
                            (target.IsGenesis
                                ? target.StartedMembershipEpoch != 1 || origin.AdditionOperationId is not null ||
                                  origin.AdditionOperationHash is not null
                                : !MatchesAdditionEvidence(target, origin)))
                        {
                            throw new InvalidDataException("The retained removal cutoff conflicts with its membership authorization.");
                        }

                        var key = (payload.RemovedDeviceId, origin.OriginInstanceId, origin.UserKeyEpoch);
                        var expected = new UserOriginRemovalCutoff
                        {
                            UserId = user.UId,
                            DeviceId = key.RemovedDeviceId,
                            OriginInstanceId = key.OriginInstanceId,
                            UserKeyEpoch = key.UserKeyEpoch,
                            HighestAcceptedSnapshotRevision = origin.HighestAcceptedSnapshotRevision,
                            HighestAcceptedControlSequence = origin.HighestAcceptedControlSequence,
                            ResultingMembershipEpoch = payload.ResultingMembershipEpoch,
                            AuthorizationId = origin.AuthorizationId,
                            RemovalOperationId = envelope.OperationId,
                            RemovalOperationHash = envelope.OperationHash.ToArray(),
                            CreatedAtUtc = envelope.CreatedAtUtc
                        };
                        if (!expectedCutoffs.TryAdd(key, expected))
                            throw new InvalidDataException("Multiple signed removals claim the same origin key namespace.");

                        if (!cutoffByNamespace.TryGetValue(key, out var retainedCutoff))
                        {
                            findings.Add("RemovalCutoffMissing");
                        }
                        else if (retainedCutoff.Length != 1 || !CutoffMatches(retainedCutoff[0], expected))
                        {
                            throw new InvalidDataException("A stored removal cutoff disagrees with the signed operation.");
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    findings.Add("SignedRemovalHistoryInvalid");
                    BackendDebugLog.Warning(
                        $"Self diagnostics found invalid signed removal history. UserId={user.UId}, OperationId={operation.OperationId}.",
                        ex,
                        "SelfDiagnostics");
                }
            }

            if (existingCutoffs.Any(row =>
                    !expectedCutoffs.ContainsKey((row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch))))
            {
                findings.Add("RemovalCutoffInvalid");
            }

            if (findings.Count != 0)
                return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };

            var metadataRepairCount = await RepairRemovedDeviceMetadataAsync(token, user.UId, lifecycleToken);
            return new SelfDiagnosticsResultResponse
            {
                Healthy = true,
                RepairedCount = metadataRepairCount,
                Findings = metadataRepairCount == 0 ? [] : ["RemovedDeviceMetadataCleaned"]
            };
        }, ct);
    }

    private async Task<int> RepairRemovedDeviceMetadataAsync(Guid token, Guid userId, CancellationToken ct)
    {
        var authorizations = await _authorizations.ListForUserAsync(userId, ct);
        var links = await _userDevices.ListByUserAsync(userId, ct);
        var activeDeviceIds = authorizations.Where(row => row.IsActive).Select(row => row.DeviceId).ToHashSet();
        activeDeviceIds.Add(_identity.LocalDeviceId);

        using var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
        var staleDevices = bundle.UserDevicesData.Devices
            .Where(device => !activeDeviceIds.Contains(device.Id) &&
                             HasVerifiedRemovalEvidence(device.Id, authorizations, links, bundle.UserDevicesData))
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
                bundle.UserDevicesData,
                repair.Device.Id,
                repair.DeletedAt,
                repair.Version);
            repair.Device.Dispose();
            bundle.UserDevicesData.Devices.Remove(repair.Device);
        }

        await _userDataWriter.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, true, ct);
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
        UserMembershipAuthorization target,
        DeviceRemovalOriginCutoffPayload origin) =>
        target.AdditionOperationId is Guid additionId && additionId == target.AuthorizationId &&
        target.AdditionOperationHash is { Length: SyncConstants.SyncDeltaPayloadHashBytes } additionHash &&
        origin.AdditionOperationId == additionId &&
        origin.AdditionOperationHash is { Length: SyncConstants.SyncDeltaPayloadHashBytes } originHash &&
        Hashing.Verify(originHash, additionHash);

    private static bool CutoffMatches(UserOriginRemovalCutoff actual, UserOriginRemovalCutoff expected) =>
        actual.UserId == expected.UserId && actual.AuthorizationId == expected.AuthorizationId &&
        actual.ResultingMembershipEpoch == expected.ResultingMembershipEpoch &&
        actual.RemovalOperationId == expected.RemovalOperationId &&
        actual.HighestAcceptedSnapshotRevision == expected.HighestAcceptedSnapshotRevision &&
        actual.HighestAcceptedControlSequence == expected.HighestAcceptedControlSequence &&
        Hashing.Verify(actual.RemovalOperationHash, expected.RemovalOperationHash);
}
