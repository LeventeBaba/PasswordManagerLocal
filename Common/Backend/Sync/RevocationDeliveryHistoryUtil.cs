using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>Verifies frozen, receipt-only historical identities and cutoffs without restoring them into live authority.</summary>
internal static class RevocationDeliveryHistoryUtil
{
    internal static UserMembershipAuthorization[] Validate(RevocationReceiptEvidence evidence,
        UserControlOperationEnvelope operation, UserMembershipAuthorization[] members)
    {
        if (operation.RevocationMembers.Count == 0 || operation.RevocationMembers.Count != members.Length)
            throw new InvalidDataException("The original historical recipient declaration is missing or incomplete.");
        foreach (var declared in operation.RevocationMembers)
        {
            var member = members.SingleOrDefault(m => m.AuthorizationId == declared.AuthorizationId);
            if (member is null || member.UserId != operation.UserId || member.DeviceId != declared.DeviceId ||
                member.OriginInstanceId != declared.OriginInstanceId || member.StartedMembershipEpoch != declared.StartedMembershipEpoch ||
                member.MinimumKeyEpoch != declared.MinimumKeyEpoch || member.AdditionOperationId != declared.AdditionOperationId ||
                member.StartedMembershipEpoch > operation.PreviousMembershipEpoch || member.MinimumKeyEpoch > operation.PreviousKeyEpoch ||
                !Hashing.Verify(member.SignPublicKeyHash, declared.SignPublicKeyHash) ||
                !Hashing.Verify(member.SignPublicKey, declared.SignPublicKey) ||
                !Hashing.Verify(member.AdditionOperationHash ?? [], declared.AdditionOperationHash ?? []) ||
                !Hashing.Verify(member.RemovalOperationHash ?? [], declared.RemovalOperationHash ?? []) ||
                !Hashing.Verify(member.SignPublicKeyHash, Hashing.SHA256Hash(member.SignPublicKey)) ||
                EndAtBase(member.EndedMembershipEpoch, operation.PreviousMembershipEpoch) != EndAtBase(declared.EndedMembershipEpoch, operation.PreviousMembershipEpoch))
                throw new InvalidDataException("The signed recipient declaration differs from historically verified membership.");
        }
        var originalAuthor = members.SingleOrDefault(m => m.DeviceId == operation.OriginDeviceId && m.OriginInstanceId == operation.OriginInstanceId);
        if (originalAuthor is null || originalAuthor.StartedMembershipEpoch > operation.PreviousMembershipEpoch ||
            originalAuthor.EndedMembershipEpoch <= operation.PreviousMembershipEpoch || originalAuthor.MinimumKeyEpoch > operation.PreviousKeyEpoch ||
            originalAuthor.MaximumKeyEpoch < operation.PreviousKeyEpoch)
            throw new InvalidDataException("The original revocation author is outside its historical authorization.");
        if (operation.OperationType == UserControlOperationType.AccountDeletion)
        {
            var deletion = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(operation.OperationPayload);
            if (deletion.KnownMembers.Count != members.Length || deletion.KnownMembers.Any(m =>
                    !operation.RevocationMembers.Any(d => SameDeclaredIdentity(d, m))))
                throw new InvalidDataException("The deletion's signed membership declarations disagree.");
        }
        if (evidence.MembershipProofPayload.Length == 0 || evidence.MembershipProofPayload.Length > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
            throw new InvalidDataException("The retained retirement proof size is invalid.");
        var proofs = JsonSerializer.Deserialize(evidence.MembershipProofPayload, BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes)
            ?? throw new InvalidDataException("The retained retirement proofs are missing.");
        if (proofs.Length > SyncConstants.MaxUserControlInventoryEntries)
            throw new InvalidDataException("Too many retained retirement proofs.");
        var genesis = members.Where(m => m.StartedMembershipEpoch == 1).ToArray();
        if (genesis.Length != 1 || genesis[0].AdditionOperationId is not null || genesis[0].MinimumKeyEpoch > operation.PreviousKeyEpoch)
            throw new InvalidDataException("The signed recipient history lacks one exact genesis anchor.");
        if (proofs.LongLength != operation.PreviousMembershipEpoch - 1)
            throw new InvalidDataException("The complete signed membership predecessor chain is unavailable.");
        var active = new HashSet<Guid> { genesis[0].AuthorizationId };
        long epoch = 1;
        foreach (var proof in proofs.OrderBy(p => p.PreviousMembershipEpoch))
        {
            if (proof.UserId != operation.UserId || proof.PreviousMembershipEpoch != epoch || proof.ResultingMembershipEpoch != epoch + 1 ||
                proof.OperationType is not (UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval))
                throw new InvalidDataException("The signed historical membership chain is missing, forked or out of namespace.");
            var author = members.SingleOrDefault(m => m.DeviceId == proof.OriginDeviceId && m.OriginInstanceId == proof.OriginInstanceId);
            if (author is null || !active.Contains(author.AuthorizationId) || author.MinimumKeyEpoch > proof.PreviousKeyEpoch ||
                author.MaximumKeyEpoch < proof.PreviousKeyEpoch)
                throw new InvalidDataException("The historical transition author was not authorized at its signed base.");
            UserControlOperationEnvelopeUtil.VerifyWithSigningKey(proof, author.SignPublicKey);
            if (proof.OperationType == UserControlOperationType.DeviceAddition)
            {
                var addition = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(proof.OperationPayload);
                var member = members.SingleOrDefault(m => m.AuthorizationId == proof.OperationId);
                if (member is null || member.AdditionOperationId != proof.OperationId || member.AdditionOperationHash is null ||
                    !Hashing.Verify(member.AdditionOperationHash, proof.OperationHash) || member.DeviceId != addition.NewDeviceId ||
                    member.OriginInstanceId != addition.NewOriginInstanceId || !Hashing.Verify(member.SignPublicKey, addition.SignPublicKey) ||
                    member.StartedMembershipEpoch != proof.ResultingMembershipEpoch || member.MinimumKeyEpoch != addition.KeyEpoch ||
                    addition.UserId != operation.UserId || addition.PreviousMembershipEpoch != proof.PreviousMembershipEpoch ||
                    addition.ResultingMembershipEpoch != proof.ResultingMembershipEpoch || addition.KeyEpoch != proof.PreviousKeyEpoch ||
                    members.Any(m => m.DeviceId == member.DeviceId && active.Contains(m.AuthorizationId)) || !active.Add(member.AuthorizationId))
                    throw new InvalidDataException("The receipt-only installation lacks its exact signed addition proof.");
            }
            else
            {
                var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(proof.OperationPayload);
                var removed = members.Where(m => m.DeviceId == removal.RemovedDeviceId && active.Contains(m.AuthorizationId)).ToArray();
                if (removed.Length == 0 || removal.UserId != operation.UserId || removal.KeyEpoch != proof.PreviousKeyEpoch ||
                    removal.PreviousMembershipEpoch != proof.PreviousMembershipEpoch || removal.ResultingMembershipEpoch != proof.ResultingMembershipEpoch ||
                    !removal.Origins.Select(o => o.AuthorizationId).ToHashSet().SetEquals(removed.Select(m => m.AuthorizationId)))
                    throw new InvalidDataException("The historical retirement lacks complete exact-installation cutoffs.");
                foreach (var member in removed)
                {
                    if (member.RemovalOperationId != proof.OperationId || member.RemovalOperationHash is null ||
                        !Hashing.Verify(member.RemovalOperationHash, proof.OperationHash) || member.EndedMembershipEpoch != proof.ResultingMembershipEpoch)
                        throw new InvalidDataException("The historical retirement reference does not match its signed proof.");
                    DeviceRemovalEvidenceUtil.ValidateAuthorization(removal, member);
                    active.Remove(member.AuthorizationId);
                }
            }
            epoch++;
        }
        if (!active.SetEquals(members.Where(m => m.EndedMembershipEpoch is null || operation.PreviousMembershipEpoch < m.EndedMembershipEpoch)
                .Select(m => m.AuthorizationId)))
            throw new InvalidDataException("The signed predecessor chain does not produce the declared recipient membership.");
        var required = members.Where(m => IsRequired(operation, m.AuthorizationId)).ToArray();
        if (operation.OperationType == UserControlOperationType.DeviceRemoval)
        {
            var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(operation.OperationPayload);
            var targets = members.Where(m => m.DeviceId == removal.RemovedDeviceId &&
                m.StartedMembershipEpoch <= operation.PreviousMembershipEpoch &&
                (m.EndedMembershipEpoch is null || operation.PreviousMembershipEpoch < m.EndedMembershipEpoch)).ToArray();
            if (targets.Length == 0 || !removal.Origins.Select(o => o.AuthorizationId).ToHashSet().SetEquals(targets.Select(m => m.AuthorizationId)))
                throw new InvalidDataException("The removed-target exemption lacks complete signed cutoffs.");
            foreach (var target in targets) DeviceRemovalEvidenceUtil.ValidateAuthorization(removal, target);
        }
        return required;
    }

    private static bool SameDeclaredIdentity(AccountDeletionKnownMember a, AccountDeletionKnownMember b) =>
        a.AuthorizationId == b.AuthorizationId && a.DeviceId == b.DeviceId && a.OriginInstanceId == b.OriginInstanceId &&
        a.StartedMembershipEpoch == b.StartedMembershipEpoch && a.EndedMembershipEpoch == b.EndedMembershipEpoch &&
        a.MinimumKeyEpoch == b.MinimumKeyEpoch && a.MaximumKeyEpoch == b.MaximumKeyEpoch &&
        a.AdditionOperationId == b.AdditionOperationId && a.RemovalOperationId == b.RemovalOperationId &&
        Hashing.Verify(a.SignPublicKeyHash, b.SignPublicKeyHash) && Hashing.Verify(a.SignPublicKey, b.SignPublicKey) &&
        Hashing.Verify(a.AdditionOperationHash ?? [], b.AdditionOperationHash ?? []) &&
        Hashing.Verify(a.RemovalOperationHash ?? [], b.RemovalOperationHash ?? []);

    private static long? EndAtBase(long? end, long epoch) => end <= epoch ? end : null;

    internal static UserControlOperationEnvelope MergeProofs(UserControlOperationEnvelope stored, UserControlOperationEnvelope incoming)
    {
        if (!Hashing.Verify(stored.OperationHash, incoming.OperationHash) || stored.OperationId != incoming.OperationId || stored.UserId != incoming.UserId)
            throw new InvalidDataException("Auxiliary membership evidence cannot change the original signed operation.");
        var merged = UserControlOperationEnvelopeUtil.CopyCoreProof(stored);
        foreach (var proof in stored.RevocationHistoryProofs.Concat(incoming.RevocationHistoryProofs))
        {
            try
            {
                var member = stored.RevocationMembers.SingleOrDefault(m =>
                    m.DeviceId == proof.OriginDeviceId && m.OriginInstanceId == proof.OriginInstanceId);
                var referenced = stored.RevocationMembers.Any(m =>
                    (m.AdditionOperationId == proof.OperationId && m.AdditionOperationHash is not null && Hashing.Verify(m.AdditionOperationHash, proof.OperationHash)) ||
                    (m.RemovalOperationId == proof.OperationId && m.RemovalOperationHash is not null && Hashing.Verify(m.RemovalOperationHash, proof.OperationHash)));
                if (member is null || !referenced || proof.UserId != stored.UserId || proof.ResultingMembershipEpoch > stored.PreviousMembershipEpoch ||
                    member.StartedMembershipEpoch > proof.PreviousMembershipEpoch || member.EndedMembershipEpoch <= proof.PreviousMembershipEpoch ||
                    member.MinimumKeyEpoch > proof.PreviousKeyEpoch || member.MaximumKeyEpoch < proof.PreviousKeyEpoch ||
                    proof.OperationType is not (UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval)) continue;
                UserControlOperationEnvelopeUtil.VerifyWithSigningKey(proof, member.SignPublicKey);
                if (!merged.RevocationHistoryProofs.Any(p => p.OperationId == proof.OperationId && Hashing.Verify(p.OperationHash, proof.OperationHash)))
                    merged.RevocationHistoryProofs.Add(UserControlOperationEnvelopeUtil.CopyCoreProof(proof));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException) { }
        }
        merged.RevocationHistoryProofs = merged.RevocationHistoryProofs.OrderBy(p => p.PreviousMembershipEpoch).ThenBy(p => p.OperationId).ToList();
        return merged;
    }

    internal static bool IsRequired(UserControlOperationEnvelope operation, Guid authorizationId)
    {
        var member = operation.RevocationMembers.SingleOrDefault(m => m.AuthorizationId == authorizationId);
        if (member is null || member.StartedMembershipEpoch > operation.PreviousMembershipEpoch ||
            member.EndedMembershipEpoch <= operation.PreviousMembershipEpoch) return false;
        return operation.OperationType != UserControlOperationType.DeviceRemoval ||
            UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(operation.OperationPayload).RemovedDeviceId != member.DeviceId;
    }
}
