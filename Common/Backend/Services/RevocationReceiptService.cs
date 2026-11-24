using System.Text.Json;
using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class RevocationReceiptService(
    IRevocationReceiptRepository receipts,
    IUserMembershipAuthorizationRepository membership,
    IUserControlOperationRepository operations,
    IDeviceIdentityService identity,
    IUserLifecycleCoordinator lifecycle,
    IUnitOfWork uow, IDeviceRepository? devices = null) : IRevocationReceiptService
{
    public async Task StageAppliedAsync(UserControlOperationEnvelope operation, CancellationToken ct = default)
    {
        if (operation.OperationType is not (UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion))
            return;
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(operation);
        var evidence = await receipts.GetEvidenceAsync(operation.OperationId, ct);
        UserMembershipAuthorization[] members;
        if (evidence is null)
        {
            var known = (await membership.ListForUserAsync(operation.UserId, ct)).Where(m =>
                m.StartedMembershipEpoch <= operation.PreviousMembershipEpoch && m.MinimumKeyEpoch <= operation.PreviousKeyEpoch).ToArray();
            members = BuildReceiptContext(operation, known);
            if (members.Length == 0 || members.Length > SyncConstants.MaxUserControlInventoryEntries)
                throw new InvalidDataException("The applied revocation lacks bounded historical authorization evidence.");
            var author = members.SingleOrDefault(m => m.DeviceId == operation.OriginDeviceId &&
                m.OriginInstanceId == operation.OriginInstanceId)
                ?? throw new InvalidDataException("The applied revocation author has no historical signing identity.");
            UserControlOperationEnvelopeUtil.VerifyWithSigningKey(operation, author.SignPublicKey);
            var retirements = RevocationDeliveryHistoryUtil.MergeProofs(operation, operation).RevocationHistoryProofs;
            foreach (var prior in (await operations.ListForUserAsync(operation.UserId, ct)).Where(o =>
                         o.Status == UserControlOperationStatus.Applied && o.ResultingMembershipEpoch <= operation.PreviousMembershipEpoch &&
                         o.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval))
            {
                var proof = UserControlOperationEnvelopeUtil.CopyCoreProof(UserControlOperationEnvelopeUtil.Deserialize(prior.EnvelopePayload));
                if (!retirements.Any(p => p.OperationId == proof.OperationId && Hashing.Verify(p.OperationHash, proof.OperationHash))) retirements.Add(proof);
            }
            var retirementPayload = JsonSerializer.SerializeToUtf8Bytes(retirements.ToArray(), BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes);
            if (retirementPayload.Length > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                throw new InvalidDataException("The retained retirement proof safety limit was reached.");
            evidence = new RevocationReceiptEvidence
            {
                OperationId = operation.OperationId, UserId = operation.UserId, OperationHash = operation.OperationHash.ToArray(),
                EnvelopePayload = UserControlOperationEnvelopeUtil.Serialize(operation),
                AuthorizationPayload = JsonSerializer.SerializeToUtf8Bytes(members,
                    BackendJsonSerializerContext.Default.RevocationReceiptAuthorizations),
                MembershipProofPayload = retirementPayload
            };
            CompactDuplicateProofs(evidence);
            await receipts.AddEvidenceAsync(evidence, ct);
        }
        else
        {
            var retained = ReadEvidence(evidence);
            if (!Hashing.Verify(evidence.OperationHash, operation.OperationHash) || evidence.UserId != operation.UserId)
                throw new InvalidDataException("The revocation receipt evidence conflicts with the applied operation.");
            members = retained.Members;
            if (!evidence.HistoryResetBlocked && !evidence.HasReceiptConflict)
            {
                var oldProofs = JsonSerializer.Deserialize(evidence.MembershipProofPayload, BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes) ?? [];
                var old = UserControlOperationEnvelopeUtil.CopyCoreProof(retained.Operation);
                old.RevocationHistoryProofs.AddRange(oldProofs);
                var merged = RevocationDeliveryHistoryUtil.MergeProofs(old, operation);
                var payload = JsonSerializer.SerializeToUtf8Bytes(merged.RevocationHistoryProofs.ToArray(), BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes);
                if (!payload.SequenceEqual(evidence.MembershipProofPayload))
                {
                    evidence.MembershipProofPayload = payload;
                    evidence.EnvelopePayload = UserControlOperationEnvelopeUtil.Serialize(merged);
                    CompactDuplicateProofs(evidence);
                    receipts.UpdateEvidence(evidence);
                }
            }
        }
        await AugmentRetainedContextsAsync(operation, ct);
        var local = members.SingleOrDefault(m => m.DeviceId == identity.LocalDeviceId && m.OriginInstanceId == identity.OriginInstanceId &&
            (m.EndedMembershipEpoch is null || operation.PreviousMembershipEpoch < m.EndedMembershipEpoch) &&
            (m.MaximumKeyEpoch is null || operation.PreviousKeyEpoch <= m.MaximumKeyEpoch));
        if (local is null || !local.SignPublicKey.SequenceEqual(identity.SignPublicKey))
            return; // Applying deletion must not fabricate authority for an unrelated local installation.
        if (operation.OperationType == UserControlOperationType.AccountDeletion)
        {
            var deletion = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(operation.OperationPayload);
            if (!deletion.KnownMembers.Any(m => m.AuthorizationId == local.AuthorizationId && m.DeviceId == local.DeviceId &&
                    m.OriginInstanceId == local.OriginInstanceId && Hashing.Verify(m.SignPublicKeyHash, local.SignPublicKeyHash) &&
                    m.StartedMembershipEpoch == local.StartedMembershipEpoch && m.MinimumKeyEpoch == local.MinimumKeyEpoch &&
                    m.AdditionOperationId == local.AdditionOperationId && m.StartedMembershipEpoch <= operation.PreviousMembershipEpoch &&
                    (m.EndedMembershipEpoch is null || operation.PreviousMembershipEpoch < m.EndedMembershipEpoch) &&
                    (m.MaximumKeyEpoch is null || operation.PreviousKeyEpoch <= m.MaximumKeyEpoch)))
                return; // A partial signed member summary cannot attest this installation; deletion still applies.
        }
        var locallyKnown = await membership.GetForSignedEpochAsync(operation.UserId, identity.LocalDeviceId, identity.OriginInstanceId,
            operation.PreviousMembershipEpoch, ct);
        if (locallyKnown is null || locallyKnown.AuthorizationId != local.AuthorizationId || !locallyKnown.SignPublicKey.SequenceEqual(identity.SignPublicKey))
        {
            try { RevocationDeliveryHistoryUtil.Validate(evidence, operation, members); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException)
            { return; } // Deletion still commits; missing/ambiguous public authorization cannot create a receipt.
        }
        var receipt = RevocationDeliveryReceiptUtil.Create(operation, local, RevocationReceiptKind.Applied, identity);
        await StageReceiptAsync(receipt, ct);
    }

    public async Task<IReadOnlyList<RevocationReceiptInventoryEntry>> GetInventoryAsync(Guid operationId, CancellationToken ct = default)
    {
        var row = await operations.GetByIdAsync(operationId, ct);
        var evidence = await receipts.GetEvidenceAsync(operationId, ct);
        // A history reset may remove live operations. Frozen evidence survives, but cannot authorize
        // traffic in the replacement history. Quarantined operations never advertise application.
        if (row?.Status != UserControlOperationStatus.Applied || evidence is null)
            return [];
        if (row.UserId != evidence.UserId || !Hashing.Verify(row.OperationHash, evidence.OperationHash))
            throw new InvalidDataException("The live revocation conflicts with retained receipt evidence.");
        var verified = ReadEvidence(evidence);
        var result = new List<RevocationReceiptInventoryEntry>();
        var rows = await receipts.ListAsync(operationId, ct);
        foreach (var stored in rows)
        {
            var receipt = VerifyStored(stored, verified.Operation, verified.Members);
            if (receipt.Kind == RevocationReceiptKind.AppliedReceiptStored) ValidateCustody(receipt, verified.Operation, rows);
            result.Add(ToInventory(receipt));
        }
        return result;
    }

    public async Task<IReadOnlyList<byte[]>> BuildRequestedAsync(IEnumerable<RevocationReceiptInventoryEntry> requests,
        IReadOnlySet<Guid> allowedOperations, CancellationToken ct = default)
    {
        var list = requests.ToArray();
        if (list.Length > SyncConstants.MaxRevocationReceiptsPerCall)
            throw new InvalidDataException("Too many revocation receipts were requested.");
        var result = new List<byte[]>();
        var seen = new HashSet<(Guid, Guid, Guid, Guid, int, string)>();
        foreach (var request in list)
        {
            var key = Key(request);
            if (!seen.Add(key)) throw new InvalidDataException("Duplicate revocation receipt request.");
            if (!allowedOperations.Contains(key.Item1))
                throw new UnauthorizedAccessException("The requested receipt is unavailable for this peer.");
            var inventory = await GetInventoryAsync(key.Item1, ct);
            if (!inventory.Any(i => Key(i) == key && Hashing.Verify(i.ReceiptHash.ToByteArray(), request.ReceiptHash.ToByteArray())))
                throw new SyncStateUnavailableException("The exact requested receipt is unavailable; refresh inventory.");
            var stored = (await receipts.ListAsync(key.Item1, ct)).Single(r =>
                r.RecipientDeviceId == key.Item2 && r.RecipientOriginInstanceId == key.Item3 &&
                r.RecipientAuthorizationId == key.Item4 && (int)r.Kind == key.Item5 && Convert.ToHexString(r.AttestedReceiptHash) == key.Item6);
            result.Add(stored.Payload.ToArray());
        }
        return result;
    }

    public async Task ObservePeerInventoryAsync(Guid peerDeviceId, IEnumerable<UserControlOperationUserInventory> inventory, CancellationToken ct = default)
    {
        if (devices is null) return; // Focused receipt-only harnesses have no authenticated transport peer.
        var peer = await devices.GetByIdAsync(peerDeviceId, ct)
            ?? throw new UnauthorizedAccessException("The evidence peer is unknown.");
        if (!peer.IsTrusted || peer.IsBlocked) throw new UnauthorizedAccessException("The evidence peer is not authorized.");
        foreach (var user in inventory)
        {
            if (!Guid.TryParse(user.UserId, out var userId) || userId == Guid.Empty)
                throw new InvalidDataException("The evidence inventory user is invalid.");
            var remote = user.Receipts.Select(r => (Entry: r, Key: Key(r))).ToDictionary(r => r.Key, r => r.Entry);
            foreach (var advertisedOperation in user.Operations)
            {
                var operationId = Guid.Parse(advertisedOperation.OperationId);
                await lifecycle.ExecuteAsync(userId, async token =>
                {
                    await using var transaction = await uow.BeginTransactionAsync(token);
                    try
                    {
                        var context = await receipts.GetEvidenceAsync(operationId, token);
                        if (context is null || context.UserId != userId || context.HistoryResetBlocked || context.HasReceiptConflict ||
                            advertisedOperation.Quarantined || !Hashing.Verify(context.OperationHash, advertisedOperation.OperationHash.ToByteArray()))
                        { await transaction.RollbackAsync(token); return; }
                        var verified = ReadEvidence(context);
                        var recipient = verified.Members.SingleOrDefault(m => m.DeviceId == peerDeviceId &&
                            IsRequired(verified.Operation, m.AuthorizationId) && m.SignPublicKey.SequenceEqual(peer.SignPublicKey));
                        if (recipient is null) { await transaction.RollbackAsync(token); return; }
                        var debts = (await receipts.ListDebtsAsync(operationId, token)).Where(d =>
                            d.PeerDeviceId == recipient.DeviceId && d.PeerOriginInstanceId == recipient.OriginInstanceId &&
                            d.PeerAuthorizationId == recipient.AuthorizationId).ToArray();
                        foreach (var row in (await receipts.ListAsync(operationId, token)).Where(r =>
                                     (r.Kind is RevocationReceiptKind.Applied or RevocationReceiptKind.AppliedReceiptStored) &&
                                     IsRequired(verified.Operation, r.RecipientAuthorizationId)))
                        {
                            var signed = VerifyStored(row, verified.Operation, verified.Members);
                            var entry = ToInventory(signed);
                            var alreadyStored = remote.TryGetValue(Key(entry), out var advertised) &&
                                Hashing.Verify(advertised.ReceiptHash.ToByteArray(), row.ReceiptHash);
                            var debt = debts.SingleOrDefault(d => Hashing.Verify(d.ReceiptHash, row.ReceiptHash));
                            if (alreadyStored && debt is not null) receipts.DeleteDebt(debt);
                            else if (!alreadyStored && debt is null)
                                await receipts.AddDebtAsync(new RevocationEvidenceDebt
                                {
                                    OperationId = operationId, PeerDeviceId = recipient.DeviceId,
                                    PeerOriginInstanceId = recipient.OriginInstanceId, PeerAuthorizationId = recipient.AuthorizationId,
                                    ReceiptHash = row.ReceiptHash.ToArray()
                                }, token);
                        }
                        await uow.SaveChangesAsync(token); await transaction.CommitAsync(token);
                    }
                    catch { await transaction.RollbackAsync(CancellationToken.None); uow.ClearTrackedChanges(); throw; }
                }, ct);
            }
        }
    }

    public async Task StoreAsync(IEnumerable<byte[]> payloads, IReadOnlySet<Guid> allowedOperations, CancellationToken ct = default)
    {
        var list = payloads.ToArray();
        if (list.Length > SyncConstants.MaxRevocationReceiptsPerCall)
            throw new InvalidDataException("Too many revocation receipt responses.");
        var seen = new HashSet<(Guid, Guid, Guid, Guid, int, string)>();
        foreach (var payload in list.OrderBy(p => RevocationDeliveryReceiptUtil.Deserialize(p).Kind == RevocationReceiptKind.AppliedReceiptStored))
        {
            var receipt = RevocationDeliveryReceiptUtil.Deserialize(payload);
            var key = Key(ToInventory(receipt));
            if (!seen.Add(key)) throw new InvalidDataException("Duplicate revocation receipt response.");
            if (!allowedOperations.Contains(receipt.OperationId))
                throw new UnauthorizedAccessException("The receipt is outside the peer's authorized control inventory.");
            var conflict = await lifecycle.ExecuteAsync(receipt.UserId, async token =>
            {
                await using var transaction = await uow.BeginTransactionAsync(token);
                try
                {
                    var evidence = await receipts.GetEvidenceAsync(receipt.OperationId, token)
                        ?? throw new SyncStateUnavailableException("Receive and apply the revocation before requesting its receipts.");
                    var retained = ReadEvidence(evidence);
                    var member = retained.Members.SingleOrDefault(m => m.AuthorizationId == receipt.RecipientAuthorizationId)
                        ?? throw new InvalidDataException("The receipt installation has no retained historical authority.");
                    RevocationDeliveryReceiptUtil.Verify(receipt, retained.Operation, member);
                    if (receipt.Kind == RevocationReceiptKind.AppliedReceiptStored)
                        ValidateCustody(receipt, retained.Operation, await receipts.ListAsync(receipt.OperationId, token));
                    var conflictDetected = await StageReceiptAsync(receipt, token);
                    await uow.SaveChangesAsync(token);
                    if (!conflictDetected) await StageCustodyAsync(evidence, retained.Operation, retained.Members, token);
                    await uow.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return conflictDetected;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    uow.ClearTrackedChanges();
                    throw;
                }
            }, ct);
            if (conflict) throw new InvalidDataException("Conflicting authenticated receipt content was retained as a delivery blocker.");
        }
    }

    private async Task AugmentRetainedContextsAsync(UserControlOperationEnvelope incoming, CancellationToken ct)
    {
        if (incoming.RevocationHistoryProofs.Count == 0) return;
        foreach (var context in (await receipts.ListEvidenceAsync(ct)).Where(e => e.UserId == incoming.UserId &&
                     e.OperationId != incoming.OperationId && !e.HistoryResetBlocked && !e.HasReceiptConflict))
        {
            try
            {
                var retained = ReadEvidence(context);
                var old = UserControlOperationEnvelopeUtil.CopyCoreProof(retained.Operation);
                old.RevocationHistoryProofs.AddRange(JsonSerializer.Deserialize(context.MembershipProofPayload,
                    BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes) ?? []);
                var candidate = UserControlOperationEnvelopeUtil.CopyCoreProof(retained.Operation);
                candidate.RevocationHistoryProofs.AddRange(incoming.RevocationHistoryProofs);
                var merged = RevocationDeliveryHistoryUtil.MergeProofs(old, candidate);
                var payload = JsonSerializer.SerializeToUtf8Bytes(merged.RevocationHistoryProofs.ToArray(),
                    BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes);
                if (payload.Length > SyncConstants.MaxIncomingDeltaTotalBytesPerCall || payload.SequenceEqual(context.MembershipProofPayload)) continue;
                context.MembershipProofPayload = payload;
                context.EnvelopePayload = UserControlOperationEnvelopeUtil.Serialize(merged);
                CompactDuplicateProofs(context);
                receipts.UpdateEvidence(context);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
            { /* Broken retained authority remains blocked; it cannot prevent a valid terminal deletion. */ }
        }
    }

    private static UserMembershipAuthorization[] BuildReceiptContext(UserControlOperationEnvelope operation,
        UserMembershipAuthorization[] known)
    {
        if (operation.RevocationMembers.Count == 0) return known;
        var result = new List<UserMembershipAuthorization>();
        foreach (var declared in operation.RevocationMembers)
        {
            var existing = known.SingleOrDefault(m => m.AuthorizationId == declared.AuthorizationId);
            // A signed receipt-only declaration can fill missing historical public identities,
            // never overwrite a known conflicting identity or a locally proven earlier retirement.
            if (existing is not null && (existing.DeviceId != declared.DeviceId || existing.OriginInstanceId != declared.OriginInstanceId ||
                !Hashing.Verify(existing.SignPublicKeyHash, declared.SignPublicKeyHash) ||
                existing.StartedMembershipEpoch != declared.StartedMembershipEpoch || existing.MinimumKeyEpoch != declared.MinimumKeyEpoch ||
                existing.AdditionOperationId != declared.AdditionOperationId ||
                !Hashing.Verify(existing.AdditionOperationHash ?? [], declared.AdditionOperationHash ?? []) ||
                (existing.EndedMembershipEpoch <= operation.PreviousMembershipEpoch && existing.EndedMembershipEpoch != declared.EndedMembershipEpoch)))
            {
                result.Add(existing);
                continue;
            }
            result.Add(new UserMembershipAuthorization
            {
                UserId = operation.UserId, AuthorizationId = declared.AuthorizationId, DeviceId = declared.DeviceId,
                OriginInstanceId = declared.OriginInstanceId, SignPublicKey = declared.SignPublicKey.ToArray(),
                SignPublicKeyHash = declared.SignPublicKeyHash.ToArray(), StartedMembershipEpoch = declared.StartedMembershipEpoch,
                EndedMembershipEpoch = declared.EndedMembershipEpoch, MinimumKeyEpoch = declared.MinimumKeyEpoch,
                MaximumKeyEpoch = declared.MaximumKeyEpoch, AdditionOperationId = declared.AdditionOperationId,
                AdditionOperationHash = declared.AdditionOperationHash?.ToArray(), RemovalOperationId = declared.RemovalOperationId,
                RemovalOperationHash = declared.RemovalOperationHash?.ToArray(), IsGenesis = declared.StartedMembershipEpoch == 1,
                IsActive = declared.EndedMembershipEpoch is null
            });
        }
        // Omitted locally known installations are explicit incomplete/conflicting-set evidence.
        result.AddRange(known.Where(m => !result.Any(r => r.AuthorizationId == m.AuthorizationId)));
        return result.OrderBy(m => m.AuthorizationId).ToArray();
    }

    private async Task StageCustodyAsync(RevocationReceiptEvidence evidence, UserControlOperationEnvelope operation,
        UserMembershipAuthorization[] members, CancellationToken ct)
    {
        if (evidence.HistoryResetBlocked || evidence.HasReceiptConflict) return;
        try { RevocationDeliveryHistoryUtil.Validate(evidence, operation, members); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException) { return; }
        var local = members.SingleOrDefault(m => m.DeviceId == identity.LocalDeviceId && m.OriginInstanceId == identity.OriginInstanceId);
        if (local is null || !local.SignPublicKey.SequenceEqual(identity.SignPublicKey) || !IsRequired(operation, local.AuthorizationId)) return;
        var rows = await receipts.ListAsync(operation.OperationId, ct);
        foreach (var applied in rows.Where(r => r.Kind == RevocationReceiptKind.Applied &&
                     r.RecipientAuthorizationId != local.AuthorizationId && IsRequired(operation, r.RecipientAuthorizationId)))
        {
            VerifyStored(applied, operation, members);
            if (rows.Any(r => r.Kind == RevocationReceiptKind.AppliedReceiptStored && r.RecipientAuthorizationId == local.AuthorizationId &&
                    Hashing.Verify(r.AttestedReceiptHash, applied.ReceiptHash))) continue;
            var custody = RevocationDeliveryReceiptUtil.Create(operation, local, RevocationReceiptKind.AppliedReceiptStored,
                identity, applied.ReceiptHash);
            await StageReceiptAsync(custody, ct);
        }
    }

    internal static bool IsRequired(UserControlOperationEnvelope operation, Guid authorizationId) =>
        RevocationDeliveryHistoryUtil.IsRequired(operation, authorizationId);

    internal static void ValidateCustody(RevocationDeliveryReceipt custody, UserControlOperationEnvelope operation,
        IReadOnlyList<StoredRevocationReceipt> rows)
    {
        var applied = rows.SingleOrDefault(r => r.Kind == RevocationReceiptKind.Applied && Hashing.Verify(r.ReceiptHash, custody.AttestedReceiptHash));
        if (applied is null) throw new SyncStateUnavailableException("Receive the exact signed application receipt before its custody proof.");
        if (applied.RecipientAuthorizationId == custody.RecipientAuthorizationId ||
            !IsRequired(operation, custody.RecipientAuthorizationId) || !IsRequired(operation, applied.RecipientAuthorizationId))
            throw new InvalidDataException("Custody must be attested by a distinct required historical installation.");
    }

    private async Task<bool> StageReceiptAsync(RevocationDeliveryReceipt receipt, CancellationToken ct)
    {
        var hash = RevocationDeliveryReceiptUtil.Hash(receipt);
        var existing = (await receipts.ListAsync(receipt.OperationId, ct)).SingleOrDefault(r =>
            r.RecipientDeviceId == receipt.RecipientDeviceId && r.RecipientOriginInstanceId == receipt.RecipientOriginInstanceId &&
            r.RecipientAuthorizationId == receipt.RecipientAuthorizationId && r.Kind == receipt.Kind &&
            Hashing.Verify(r.AttestedReceiptHash, receipt.AttestedReceiptHash));
        if (existing is not null)
        {
            if (!Hashing.Verify(existing.ReceiptHash, hash))
            {
                var context = await receipts.GetEvidenceAsync(receipt.OperationId, ct)
                    ?? throw new InvalidDataException("Conflicting receipt context is missing.");
                if (!context.HasReceiptConflict)
                {
                    context.HasReceiptConflict = true;
                    receipts.UpdateEvidence(context);
                }
                return true;
            }
            return false;
        }
        await receipts.AddAsync(new StoredRevocationReceipt
        {
            OperationId = receipt.OperationId, UserId = receipt.UserId, RecipientDeviceId = receipt.RecipientDeviceId,
            RecipientOriginInstanceId = receipt.RecipientOriginInstanceId, RecipientAuthorizationId = receipt.RecipientAuthorizationId,
            Kind = receipt.Kind, AttestedReceiptHash = receipt.AttestedReceiptHash.ToArray(), ReceiptHash = hash, Payload = RevocationDeliveryReceiptUtil.Serialize(receipt)
        }, ct);
        return false;
    }

    private static void CompactDuplicateProofs(RevocationReceiptEvidence evidence)
    {
        var (reason, compact) = RevocationEvidenceCompactionPolicy.Analyze(evidence);
        if (reason == Persistence.StorageRetentionReason.SafeToCompact && compact is not null)
            evidence.EnvelopePayload = compact;
    }

    internal static (UserControlOperationEnvelope Operation, UserMembershipAuthorization[] Members) ReadEvidence(RevocationReceiptEvidence evidence)
    {
        var operation = UserControlOperationEnvelopeUtil.Deserialize(evidence.EnvelopePayload);
        if (operation.OperationId != evidence.OperationId || operation.UserId != evidence.UserId ||
            !Hashing.Verify(operation.OperationHash, evidence.OperationHash))
            throw new InvalidDataException("The frozen revocation evidence is inconsistent.");
        var members = JsonSerializer.Deserialize(evidence.AuthorizationPayload,
            BackendJsonSerializerContext.Default.RevocationReceiptAuthorizations)
            ?? throw new InvalidDataException("The frozen revocation signing history is missing.");
        if (members.Length == 0 || members.Length > SyncConstants.MaxUserControlInventoryEntries ||
            members.Select(m => m.AuthorizationId).Distinct().Count() != members.Length ||
            members.Select(m => (m.DeviceId, m.OriginInstanceId)).Distinct().Count() != members.Length)
            throw new InvalidDataException("The frozen revocation signing history is invalid.");
        var author = members.SingleOrDefault(m => m.DeviceId == operation.OriginDeviceId && m.OriginInstanceId == operation.OriginInstanceId)
            ?? throw new InvalidDataException("The original revocation signing identity is missing.");
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(operation, author.SignPublicKey);
        return (operation, members);
    }

    internal static RevocationDeliveryReceipt VerifyStored(StoredRevocationReceipt stored, UserControlOperationEnvelope operation,
        UserMembershipAuthorization[] members)
    {
        var receipt = RevocationDeliveryReceiptUtil.Deserialize(stored.Payload);
        var member = members.SingleOrDefault(m => m.AuthorizationId == receipt.RecipientAuthorizationId)
            ?? throw new InvalidDataException("The stored receipt signer is unknown.");
        RevocationDeliveryReceiptUtil.Verify(receipt, operation, member);
        if (stored.OperationId != receipt.OperationId || stored.UserId != receipt.UserId ||
            stored.RecipientDeviceId != receipt.RecipientDeviceId || stored.RecipientOriginInstanceId != receipt.RecipientOriginInstanceId ||
            stored.RecipientAuthorizationId != receipt.RecipientAuthorizationId || stored.Kind != receipt.Kind ||
            !Hashing.Verify(stored.AttestedReceiptHash, receipt.AttestedReceiptHash) ||
            !Hashing.Verify(stored.ReceiptHash, RevocationDeliveryReceiptUtil.Hash(receipt)))
            throw new InvalidDataException("The stored receipt identity or hash is inconsistent.");
        return receipt;
    }

    public static RevocationReceiptInventoryEntry ToInventory(RevocationDeliveryReceipt r) => new()
    {
        OperationId = r.OperationId.ToString("N"), RecipientDeviceId = r.RecipientDeviceId.ToString("N"),
        RecipientOriginInstanceId = r.RecipientOriginInstanceId.ToString("N"), RecipientAuthorizationId = r.RecipientAuthorizationId.ToString("N"),
        Kind = (int)r.Kind, AttestedReceiptHash = ByteString.CopyFrom(r.AttestedReceiptHash), ReceiptHash = ByteString.CopyFrom(RevocationDeliveryReceiptUtil.Hash(r))
    };

    public static (Guid, Guid, Guid, Guid, int, string) Key(RevocationReceiptInventoryEntry e)
    {
        if (!Guid.TryParse(e.OperationId, out var operation) || operation == Guid.Empty ||
            !Guid.TryParse(e.RecipientDeviceId, out var device) || device == Guid.Empty ||
            !Guid.TryParse(e.RecipientOriginInstanceId, out var instance) || instance == Guid.Empty ||
            !Guid.TryParse(e.RecipientAuthorizationId, out var authorization) || authorization == Guid.Empty ||
            !Enum.IsDefined((RevocationReceiptKind)e.Kind) || e.Kind is < 1 or > 3 ||
            (e.Kind == 3 ? e.AttestedReceiptHash.Length != 32 : e.AttestedReceiptHash.Length != 0) ||
            e.ReceiptHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The revocation receipt inventory identity is invalid.");
        return (operation, device, instance, authorization, e.Kind, Convert.ToHexString(e.AttestedReceiptHash.ToByteArray()));
    }
}
