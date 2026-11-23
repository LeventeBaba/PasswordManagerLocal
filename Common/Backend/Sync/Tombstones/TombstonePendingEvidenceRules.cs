using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

internal static class TombstonePendingEvidenceRules
{
    /// <summary>
    /// Only already-merged causal ancestors of the exact deletion publication can be ignored.
    /// A reporting snapshot that merely observes the deletion could contain a later competing
    /// edit and is never sufficient. All inputs must already have passed author/row verification.
    /// </summary>
    internal static TombstoneStabilityEvaluation Evaluate(
        TombstoneCausalReference reference,
        IReadOnlyList<UserSnapshotEnvelope> pending,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> knowledge,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> receipts) =>
        CreateEvaluator(pending, knowledge, receipts)(reference);

    internal static Func<TombstoneCausalReference, TombstoneStabilityEvaluation> CreateEvaluator(
        IReadOnlyList<UserSnapshotEnvelope> pending,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> knowledge,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> receipts)
    {
        var ordered = pending.OrderBy(item => item.OriginDeviceId).ThenBy(item => item.OriginInstanceId)
            .ThenBy(item => item.UserKeyEpoch).ThenBy(item => item.OriginRevision).ToArray();
        var cache = new Dictionary<(Guid Device, Guid Instance, long Key, long Membership, long Revision), TombstoneStabilityEvaluation>();
        return reference =>
        {
            var identity = (reference.OriginDeviceId, reference.OriginInstanceId, reference.UserKeyEpoch,
                reference.MembershipEpoch, reference.OriginRevision);
            if (cache.TryGetValue(identity, out var result)) return result;
            result = EvaluateOrdered(reference, ordered, knowledge, receipts);
            cache.Add(identity, result);
            return result;
        };
    }

    private static TombstoneStabilityEvaluation EvaluateOrdered(TombstoneCausalReference reference,
        UserSnapshotEnvelope[] pending,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> knowledge,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> receipts)
    {
        if (pending.Length == 0) return new(TombstoneGarbageCollectionReason.Stable);
        var anchor = receipts.TryGetValue((reference.OriginDeviceId, reference.OriginInstanceId), out var originReceipts)
            ? originReceipts.SingleOrDefault(envelope => envelope.UserKeyEpoch == reference.UserKeyEpoch &&
                envelope.MembershipEpoch == reference.MembershipEpoch && envelope.OriginRevision == reference.OriginRevision)
            : null;
        foreach (var envelope in pending)
        {
            if (anchor is null || anchor.UserId != envelope.UserId ||
                anchor.UserKeyEpoch < envelope.UserKeyEpoch || anchor.MembershipEpoch < envelope.MembershipEpoch ||
                !knowledge.TryGetValue((envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch), out var known) ||
                known.HighestStoredRevision != envelope.OriginRevision || known.HighestMergedRevision < envelope.OriginRevision ||
                !Hashing.Verify(known.HighestStoredSnapshotHash, envelope.SnapshotHash) ||
                !Covers(anchor, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch, envelope.OriginRevision) ||
                !envelope.Coverage.All(entry => Covers(anchor, entry.OriginDeviceId, entry.OriginInstanceId,
                    entry.UserKeyEpoch, entry.OriginRevision)))
            {
                return new(TombstoneGarbageCollectionReason.PendingSnapshotMerge,
                    envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch);
            }
        }
        return new(TombstoneGarbageCollectionReason.Stable);
    }

    private static bool Covers(UserSnapshotEnvelope anchor, Guid device, Guid instance, long epoch, long revision) =>
        (anchor.OriginDeviceId == device && anchor.OriginInstanceId == instance &&
         anchor.UserKeyEpoch == epoch && anchor.OriginRevision >= revision) ||
        anchor.Coverage.Any(entry => entry.OriginDeviceId == device && entry.OriginInstanceId == instance &&
            entry.UserKeyEpoch == epoch && entry.OriginRevision >= revision);
}
