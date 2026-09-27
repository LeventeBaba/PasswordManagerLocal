using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Internal.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

internal static class TombstoneGarbageCollectionRules
{
    internal static void ValidateEvidenceStructure(
        Guid userId,
        long currentKeyEpoch,
        long currentMembershipEpoch,
        IReadOnlyList<UserMembershipAuthorization> authorizations,
        IReadOnlyList<UserOriginRemovalCutoff> cutoffs,
        IReadOnlyList<UserRevisionKnowledge> knowledge)
    {
        if (userId == Guid.Empty || currentKeyEpoch <= 0 || currentMembershipEpoch <= 0)
            throw new InvalidDataException("The canonical causal-evidence scope is invalid.");

        var authorizationById = new Dictionary<Guid, UserMembershipAuthorization>();
        var exactInstallations = new HashSet<(Guid DeviceId, Guid OriginInstanceId)>();
        var activeDevices = new HashSet<Guid>();
        var membershipTransitionEpochs = new HashSet<long>();
        var genesisCount = 0;
        foreach (var row in authorizations)
        {
            if (row.UserId != userId || row.AuthorizationId == Guid.Empty || row.DeviceId == Guid.Empty ||
                row.OriginInstanceId == Guid.Empty || row.SignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
                row.SignPublicKeyHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
                row.AgreementPublicKeyHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
                !Hashing.Verify(row.SignPublicKeyHash, Hashing.SHA256Hash(row.SignPublicKey)) ||
                string.IsNullOrWhiteSpace(row.TlsCertFingerprint) || !DeviceTypeDetector.IsValid(row.DeviceType) ||
                row.StartedMembershipEpoch <= 0 || row.StartedMembershipEpoch > currentMembershipEpoch ||
                row.MinimumKeyEpoch <= 0 || row.MinimumKeyEpoch > currentKeyEpoch ||
                !authorizationById.TryAdd(row.AuthorizationId, row) ||
                !exactInstallations.Add((row.DeviceId, row.OriginInstanceId)))
            {
                throw new InvalidDataException("Membership history contains invalid or conflicting causal evidence.");
            }

            _ = SyncIdentityUtil.NormalizeFingerprint(row.TlsCertFingerprint);

            var hasAdditionId = row.AdditionOperationId is not null;
            var hasAdditionHash = row.AdditionOperationHash is not null;
            if (hasAdditionId != hasAdditionHash ||
                (row.AdditionOperationId is Guid additionId &&
                 (additionId == Guid.Empty || row.AdditionOperationHash!.Length != SyncConstants.SyncDeltaPayloadHashBytes)))
            {
                throw new InvalidDataException("Membership addition evidence is incomplete.");
            }

            if (row.IsGenesis)
            {
                genesisCount++;
                if (row.StartedMembershipEpoch != 1 || hasAdditionId)
                    throw new InvalidDataException("Genesis membership evidence is invalid.");
            }
            else
            {
                if (row.StartedMembershipEpoch <= 1 || !hasAdditionId ||
                    !membershipTransitionEpochs.Add(row.StartedMembershipEpoch))
                {
                    throw new InvalidDataException("Membership addition history is incomplete or conflicting.");
                }
            }

            if (row.IsActive)
            {
                if (!activeDevices.Add(row.DeviceId) || row.EndedMembershipEpoch is not null ||
                    row.MaximumKeyEpoch is not null || row.RemovalOperationId is not null ||
                    row.RemovalOperationHash is not null || row.EndedAtUtc is not null)
                {
                    throw new InvalidDataException("Active membership history contains removal evidence or duplicate device authorization.");
                }
            }
            else
            {
                if (row.EndedMembershipEpoch is not long endedMembershipEpoch ||
                    endedMembershipEpoch <= row.StartedMembershipEpoch || endedMembershipEpoch > currentMembershipEpoch ||
                    row.MaximumKeyEpoch is not long maximumKeyEpoch ||
                    maximumKeyEpoch < row.MinimumKeyEpoch || maximumKeyEpoch > currentKeyEpoch ||
                    row.RemovalOperationId is not Guid removalId || removalId == Guid.Empty ||
                    row.RemovalOperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes } ||
                    row.EndedAtUtc is null ||
                    !membershipTransitionEpochs.Add(endedMembershipEpoch))
                {
                    throw new InvalidDataException("Ended membership history is incomplete or conflicting.");
                }
            }
        }

        if (genesisCount != 1 || activeDevices.Count == 0 ||
            membershipTransitionEpochs.Count != currentMembershipEpoch - 1)
        {
            throw new InvalidDataException("The historical membership transition chain is incomplete.");
        }

        var cutoffNamespaces = new HashSet<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch)>();
        var cutoffIds = new HashSet<Guid>();
        foreach (var row in cutoffs)
        {
            if (row.UserId != userId || row.CutoffId == Guid.Empty || row.DeviceId == Guid.Empty ||
                row.OriginInstanceId == Guid.Empty || row.UserKeyEpoch <= 0 || row.UserKeyEpoch > currentKeyEpoch ||
                row.HighestAcceptedSnapshotRevision < 0 || row.HighestAcceptedControlSequence < 0 ||
                row.ResultingMembershipEpoch <= 0 || row.ResultingMembershipEpoch > currentMembershipEpoch ||
                row.AuthorizationId == Guid.Empty || row.RemovalOperationId == Guid.Empty ||
                row.RemovalOperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
                !cutoffIds.Add(row.CutoffId) ||
                !cutoffNamespaces.Add((row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch)) ||
                !authorizationById.TryGetValue(row.AuthorizationId, out var authorization) ||
                authorization.IsActive || authorization.DeviceId != row.DeviceId ||
                authorization.OriginInstanceId != row.OriginInstanceId ||
                authorization.RemovalOperationId != row.RemovalOperationId ||
                authorization.EndedMembershipEpoch != row.ResultingMembershipEpoch ||
                authorization.RemovalOperationHash is null ||
                !Hashing.Verify(authorization.RemovalOperationHash, row.RemovalOperationHash) ||
                row.UserKeyEpoch < authorization.MinimumKeyEpoch ||
                (authorization.MaximumKeyEpoch is long maximum && row.UserKeyEpoch > maximum))
            {
                throw new InvalidDataException("Removal-cutoff history contains invalid or conflicting causal evidence.");
            }
        }

        var cutoffsByAuthorization = cutoffs
            .GroupBy(row => row.AuthorizationId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.UserKeyEpoch).ToArray());
        foreach (var authorization in authorizations)
        {
            if (authorization.IsActive)
            {
                if (cutoffsByAuthorization.ContainsKey(authorization.AuthorizationId))
                    throw new InvalidDataException("An active membership authorization has removal cutoffs.");
                continue;
            }

            var maximumKeyEpoch = authorization.MaximumKeyEpoch
                ?? throw new InvalidDataException("An ended membership authorization has no maximum key epoch.");
            var expectedCutoffCount = checked(maximumKeyEpoch - authorization.MinimumKeyEpoch + 1);
            if (!cutoffsByAuthorization.TryGetValue(authorization.AuthorizationId, out var authorizationCutoffs) ||
                authorizationCutoffs.LongLength != expectedCutoffCount)
            {
                throw new InvalidDataException("Removal-cutoff history is incomplete for an ended installation.");
            }

            for (var index = 0; index < authorizationCutoffs.Length; index++)
            {
                if (authorizationCutoffs[index].UserKeyEpoch != checked(authorization.MinimumKeyEpoch + index))
                    throw new InvalidDataException("Removal-cutoff key-epoch history contains a gap.");
            }
        }

        var knowledgeNamespaces = new HashSet<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch)>();
        foreach (var row in knowledge)
        {
            if (row.UserId != userId || row.OriginDeviceId == Guid.Empty || row.OriginInstanceId == Guid.Empty ||
                row.UserKeyEpoch <= 0 || row.UserKeyEpoch > currentKeyEpoch ||
                row.HighestStoredRevision < 0 || row.HighestMergedRevision < 0 ||
                (row.HighestStoredRevision == 0 && row.HighestStoredSnapshotHash.Length != 0) ||
                (row.HighestStoredRevision > 0 && row.HighestStoredSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes) ||
                !knowledgeNamespaces.Add((row.OriginDeviceId, row.OriginInstanceId, row.UserKeyEpoch)) ||
                !authorizations.Any(authorization =>
                    authorization.DeviceId == row.OriginDeviceId &&
                    authorization.OriginInstanceId == row.OriginInstanceId &&
                    authorization.MinimumKeyEpoch <= row.UserKeyEpoch &&
                    (authorization.MaximumKeyEpoch is null || authorization.MaximumKeyEpoch >= row.UserKeyEpoch)))
            {
                throw new InvalidDataException("Revision knowledge contains invalid or unknown causal evidence.");
            }
        }
    }

    internal static TombstoneStabilityEvaluation Evaluate(
        Guid userId,
        TombstoneDescriptor tombstone,
        IReadOnlyList<UserMembershipAuthorization> authorizations,
        IReadOnlyDictionary<Guid, UserOriginRemovalCutoff[]> cutoffByAuthorization,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> knowledge,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> receipts)
    {
        var reference = tombstone.CausalReference;
        if (reference is null || !reference.IsValid)
            return new(TombstoneGarbageCollectionReason.MissingCausalReference);
        if (!tombstone.Version.IsValid ||
            reference.OriginDeviceId != tombstone.Version.OriginDeviceId ||
            reference.OriginInstanceId != tombstone.Version.OriginInstanceId)
        {
            return new(TombstoneGarbageCollectionReason.InvalidCausalReference);
        }

        if (!knowledge.TryGetValue(
                (reference.OriginDeviceId, reference.OriginInstanceId, reference.UserKeyEpoch),
                out var anchorKnowledge) ||
            anchorKnowledge.HighestMergedRevision < reference.OriginRevision)
        {
            return new(
                TombstoneGarbageCollectionReason.StoredOnlyKnowledge,
                reference.OriginDeviceId,
                reference.OriginInstanceId,
                reference.UserKeyEpoch);
        }

        var relevant = authorizations
            .Where(row => row.UserId == userId &&
                          row.StartedMembershipEpoch <= reference.MembershipEpoch &&
                          (row.EndedMembershipEpoch is null || row.EndedMembershipEpoch > reference.MembershipEpoch) &&
                          row.MinimumKeyEpoch <= reference.UserKeyEpoch &&
                          (row.MaximumKeyEpoch is null || row.MaximumKeyEpoch >= reference.UserKeyEpoch))
            .OrderBy(row => row.DeviceId)
            .ThenBy(row => row.OriginInstanceId)
            .ToArray();
        if (relevant.Length == 0 || relevant.All(row =>
                row.DeviceId != reference.OriginDeviceId || row.OriginInstanceId != reference.OriginInstanceId))
        {
            return new(TombstoneGarbageCollectionReason.UnknownHistoricalMembership);
        }

        foreach (var member in relevant)
        {
            if (!member.IsActive && member.EndedMembershipEpoch > reference.MembershipEpoch)
            {
                var removal = EvaluateRemovedMember(member, reference, cutoffByAuthorization, knowledge);
                if (removal.Reason != TombstoneGarbageCollectionReason.Stable)
                    return removal;
                continue;
            }

            if (!receipts.TryGetValue((member.DeviceId, member.OriginInstanceId), out var memberReceipts) ||
                !memberReceipts.Any(envelope => Covers(envelope, reference)))
            {
                return new(
                    TombstoneGarbageCollectionReason.MissingMergedReceipt,
                    member.DeviceId,
                    member.OriginInstanceId,
                    reference.UserKeyEpoch);
            }
        }

        return new(TombstoneGarbageCollectionReason.Stable);
    }

    private static TombstoneStabilityEvaluation EvaluateRemovedMember(
        UserMembershipAuthorization member,
        TombstoneCausalReference reference,
        IReadOnlyDictionary<Guid, UserOriginRemovalCutoff[]> cutoffByAuthorization,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> knowledge)
    {
        if (member.RemovalOperationId is null || member.RemovalOperationHash is null ||
            member.EndedMembershipEpoch is null ||
            !cutoffByAuthorization.TryGetValue(member.AuthorizationId, out var cutoffs) ||
            cutoffs.Length == 0 ||
            cutoffs.All(cutoff => cutoff.UserKeyEpoch != reference.UserKeyEpoch))
        {
            return new(
                TombstoneGarbageCollectionReason.MissingRemovalCutoff,
                member.DeviceId,
                member.OriginInstanceId);
        }

        foreach (var cutoff in cutoffs)
        {
            if (cutoff.DeviceId != member.DeviceId ||
                cutoff.OriginInstanceId != member.OriginInstanceId ||
                cutoff.RemovalOperationId != member.RemovalOperationId ||
                cutoff.ResultingMembershipEpoch != member.EndedMembershipEpoch ||
                !Hashing.Verify(cutoff.RemovalOperationHash, member.RemovalOperationHash))
            {
                return new(
                    TombstoneGarbageCollectionReason.InvalidAuthenticatedEvidence,
                    member.DeviceId,
                    member.OriginInstanceId,
                    cutoff.UserKeyEpoch);
            }

            if (cutoff.HighestAcceptedSnapshotRevision == 0)
                continue;

            if (!knowledge.TryGetValue((member.DeviceId, member.OriginInstanceId, cutoff.UserKeyEpoch), out var row) ||
                row.HighestMergedRevision < cutoff.HighestAcceptedSnapshotRevision)
            {
                return new(
                    TombstoneGarbageCollectionReason.RemovalCutoffNotMerged,
                    member.DeviceId,
                    member.OriginInstanceId,
                    cutoff.UserKeyEpoch);
            }
        }

        return new(TombstoneGarbageCollectionReason.Stable);
    }

    internal static bool Covers(UserSnapshotEnvelope envelope, TombstoneCausalReference reference)
    {
        if (envelope.UserKeyEpoch < reference.UserKeyEpoch)
            return false;
        if (envelope.OriginDeviceId == reference.OriginDeviceId &&
            envelope.OriginInstanceId == reference.OriginInstanceId &&
            envelope.UserKeyEpoch == reference.UserKeyEpoch &&
            envelope.OriginRevision >= reference.OriginRevision)
        {
            return true;
        }

        return envelope.Coverage.Any(entry =>
            entry.OriginDeviceId == reference.OriginDeviceId &&
            entry.OriginInstanceId == reference.OriginInstanceId &&
            entry.UserKeyEpoch == reference.UserKeyEpoch &&
            entry.OriginRevision >= reference.OriginRevision);
    }

    internal static void SortRemainingTombstones(UserDataBundle bundle)
    {
        bundle.UserPasswordsData.DeletedPasswords.Sort((left, right) => left.Id.CompareTo(right.Id));
        bundle.UserPasswordsData.DeletedTags.Sort((left, right) => left.Id.CompareTo(right.Id));
        bundle.UserPasswordsData.DeletedCustomColors.Sort((left, right) => left.Id.CompareTo(right.Id));
        bundle.UserDevicesData.DeletedDevices.Sort((left, right) => left.Id.CompareTo(right.Id));
    }

    internal static IEnumerable<TombstoneDescriptor> EnumerateTombstones(UserDataBundle bundle)
    {
        foreach (var item in bundle.UserPasswordsData.DeletedPasswords)
            yield return new(TombstoneItemType.Password, item.Id, item.Version, item.CausalReference,
                () => Remove(bundle.UserPasswordsData.DeletedPasswords, item, UserDataBlobKind.Passwords));
        foreach (var item in bundle.UserPasswordsData.DeletedTags)
            yield return new(TombstoneItemType.PasswordTag, item.Id, item.Version, item.CausalReference,
                () => Remove(bundle.UserPasswordsData.DeletedTags, item, UserDataBlobKind.Passwords));
        foreach (var item in bundle.UserPasswordsData.DeletedCustomColors)
            yield return new(TombstoneItemType.CustomUserColor, item.Id, item.Version, item.CausalReference,
                () => Remove(bundle.UserPasswordsData.DeletedCustomColors, item, UserDataBlobKind.Passwords));
        foreach (var item in bundle.UserDevicesData.DeletedDevices)
            yield return new(TombstoneItemType.EncryptedUserDevice, item.Id, item.Version, item.CausalReference,
                () => Remove(bundle.UserDevicesData.DeletedDevices, item, UserDataBlobKind.Devices));
    }

    private static UserDataBlobKind Remove<T>(List<T> list, T item, UserDataBlobKind blob)
    {
        if (!list.Remove(item))
            throw new InvalidOperationException("A tombstone selected for collection was no longer present.");
        return blob;
    }
}
