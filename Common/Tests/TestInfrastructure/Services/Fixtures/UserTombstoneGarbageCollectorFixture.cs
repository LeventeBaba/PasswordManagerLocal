using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Tests.Backend.Services;
namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed class UserTombstoneGarbageCollectorFixture
{
    private UserTombstoneGarbageCollectorFixture(bool includeOrigin, bool includePeer)
    {
        UserId = Guid.NewGuid();
        Origin = new UserTombstoneGarbageCollectorMember(Guid.NewGuid(), Guid.NewGuid());
        Peer = new UserTombstoneGarbageCollectorMember(Guid.NewGuid(), Guid.NewGuid());
        Version = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = 1_700_000_000_000,
            LogicalCounter = 3,
            OriginDeviceId = Origin.DeviceId,
            OriginInstanceId = Origin.InstanceId
        };
        Reference = new TombstoneCausalReference
        {
            OriginDeviceId = Origin.DeviceId,
            OriginInstanceId = Origin.InstanceId,
            UserKeyEpoch = 1,
            MembershipEpoch = 2,
            OriginRevision = 7
        };
        Descriptor = new TombstoneDescriptor(
            TombstoneItemType.Password,
            Guid.NewGuid(),
            Version,
            Reference,
            () => UserDataBlobKind.Passwords);

        if (includeOrigin)
            Authorizations.Add(CreateAuthorization(Origin));
        if (includePeer)
            Authorizations.Add(CreateAuthorization(Peer));
        Knowledge[AnchorKey] = CreateKnowledge(Reference, 7, 7);
    }

    public Guid UserId { get; }
    public UserTombstoneGarbageCollectorMember Origin { get; }
    public UserTombstoneGarbageCollectorMember Peer { get; }
    public SyncVersionStamp Version { get; }
    public TombstoneCausalReference Reference { get; }
    public TombstoneDescriptor Descriptor { get; }
    public List<UserMembershipAuthorization> Authorizations { get; } = [];
    public Dictionary<Guid, UserOriginRemovalCutoff[]> Cutoffs { get; } = [];
    public Dictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge> Knowledge { get; } = [];
    public (Guid DeviceId, Guid OriginInstanceId, long KeyEpoch) AnchorKey =>
        (Reference.OriginDeviceId, Reference.OriginInstanceId, Reference.UserKeyEpoch);

    public static UserTombstoneGarbageCollectorFixture Create(bool includeOrigin = true, bool includePeer = true) =>
        new(includeOrigin, includePeer);

    public TombstoneStabilityEvaluation Evaluate(
        IReadOnlyList<UserMembershipAuthorization>? authorizations = null,
        IReadOnlyDictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>>? receipts = null) =>
        TombstoneGarbageCollectionRules.Evaluate(
            UserId,
            Descriptor,
            authorizations ?? Authorizations,
            Cutoffs,
            Knowledge,
            receipts ?? new Dictionary<(Guid, Guid), List<UserSnapshotEnvelope>>());

    public Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> ReceiptsFor(params UserTombstoneGarbageCollectorMember[] members) =>
        ReceiptsForKeyEpoch(1, members);

    public Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>> ReceiptsForKeyEpoch(
        long keyEpoch,
        params UserTombstoneGarbageCollectorMember[] members) =>
        members.ToDictionary(
            member => (member.DeviceId, member.InstanceId),
            member => new List<UserSnapshotEnvelope> { UserTombstoneGarbageCollectorTests.Receipt(member, keyEpoch, Reference) });

    public void AddRemovalEvidence(UserMembershipAuthorization authorization, long acceptedRevision, long? mergedRevision)
    {
        var operationId = Guid.NewGuid();
        var operationHash = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        authorization.UserId = UserId;
        authorization.RemovalOperationId = operationId;
        authorization.RemovalOperationHash = operationHash;
        Cutoffs[authorization.AuthorizationId] =
        [
            new UserOriginRemovalCutoff
            {
                UserId = UserId,
                DeviceId = authorization.DeviceId,
                OriginInstanceId = authorization.OriginInstanceId,
                UserKeyEpoch = 1,
                HighestAcceptedSnapshotRevision = acceptedRevision,
                ResultingMembershipEpoch = authorization.EndedMembershipEpoch!.Value,
                AuthorizationId = authorization.AuthorizationId,
                RemovalOperationId = operationId,
                RemovalOperationHash = operationHash.ToArray()
            }
        ];
        if (mergedRevision is long merged)
        {
            Knowledge[(authorization.DeviceId, authorization.OriginInstanceId, 1)] = new UserRevisionKnowledge
            {
                UserId = UserId,
                OriginDeviceId = authorization.DeviceId,
                OriginInstanceId = authorization.OriginInstanceId,
                UserKeyEpoch = 1,
                HighestStoredRevision = acceptedRevision,
                HighestMergedRevision = merged
            };
        }
    }

    private UserMembershipAuthorization CreateAuthorization(UserTombstoneGarbageCollectorMember member)
    {
        var authorization = UserTombstoneGarbageCollectorTests.Authorization(member.DeviceId, member.InstanceId, 1, null, true);
        authorization.UserId = UserId;
        return authorization;
    }

    private UserRevisionKnowledge CreateKnowledge(TombstoneCausalReference reference, long stored, long merged)
    {
        var knowledge = UserTombstoneGarbageCollectorTests.Knowledge(reference, stored, merged);
        knowledge.UserId = UserId;
        return knowledge;
    }
}
