using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserControlOperationAntiEntropyService : IUserControlOperationAntiEntropyService
{
    private readonly IUserControlOperationRepository _operations;
    private readonly IDeviceRepository _devices;
    private readonly ISyncRouteRepository _routes;
    private readonly IOutgoingDeltaBuilderService _deltaBuilder;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserMembershipAuthorizationRepository? _membershipHistory;
    private readonly IRevocationReceiptService? _receipts;

    public UserControlOperationAntiEntropyService(
        IUserControlOperationRepository operations,
        IDeviceRepository devices,
        ISyncRouteRepository routes,
        IOutgoingDeltaBuilderService deltaBuilder,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationRepository? membershipHistory = null,
        IRevocationReceiptService? receipts = null)
    {
        _operations = operations;
        _devices = devices;
        _routes = routes;
        _deltaBuilder = deltaBuilder;
        _identity = identity;
        _membershipHistory = membershipHistory;
        _receipts = receipts;
    }

    public async Task<UserControlOperationInventoryExchangeRequest> BuildInventoryAsync(
        Guid peerDeviceId,
        CancellationToken ct = default)
    {
        await ValidatePeerAsync(peerDeviceId, ct);
        var rows = await _operations.ListAllRelayableAsync(ct);
        var rowsByUser = rows.GroupBy(operation => operation.UserId).ToDictionary(group => group.Key);
        var activeUserIds = (await _routes.ListAllEligibleUserIdsAsync(peerDeviceId, ct)).ToHashSet();
        IReadOnlyList<Guid> historicalUserIds = _membershipHistory is null
            ? Array.Empty<Guid>()
            : await _membershipHistory.ListUserIdsForDeviceAsync(peerDeviceId, ct);
        // Historical authorization alone is enough to advertise an empty control-plane inventory.
        // That lets a device which has no canonical User row yet discover and request a deletion
        // operation from a relay without exposing unrelated account identities.
        var eligibleUserIds = activeUserIds
            .Concat(historicalUserIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (eligibleUserIds.Count > SyncConstants.MaxUserControlInventoryUsers)
            throw new InvalidOperationException("The control-operation inventory user limit was reached.");

        var result = new UserControlOperationInventoryExchangeRequest();
        var total = 0;
        var totalReceipts = 0;
        foreach (var userId in eligibleUserIds.OrderBy(id => id))
        {
            var userInventory = new UserControlOperationUserInventory { UserId = userId.ToString("N") };
            if (rowsByUser.TryGetValue(userId, out var group))
            {
                var relayCutoff = GetHistoricalRelayBound(group, peerDeviceId);
                foreach (var row in group
                             .Where(operation => activeUserIds.Contains(userId) ||
                                                 (operation.OperationType == UserControlOperationType.AccountDeletion && operation.Status == UserControlOperationStatus.Applied) ||
                                                 (operation.Status == UserControlOperationStatus.Applied &&
                                                  operation.ResultingMembershipEpoch <= relayCutoff.Membership &&
                                                  operation.ResultingKeyEpoch <= relayCutoff.Key))
                             .OrderBy(operation => operation.OriginDeviceId)
                             .ThenBy(operation => operation.OriginInstanceId)
                             .ThenBy(operation => operation.OriginSequence))
                {
                    total++;
                    if (total > SyncConstants.MaxUserControlInventoryEntries)
                        throw new InvalidOperationException("The control-operation inventory safety limit was reached.");

                    var envelope = UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload);
                    EnsureRowMatchesEnvelope(row, envelope);
                    userInventory.Operations.Add(ToInventoryEntry(row));
                    if (_receipts is not null && row.Status == UserControlOperationStatus.Applied &&
                        row.OperationType is UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion)
                    {
                        var receipts = await _receipts.GetInventoryAsync(row.OperationId, ct);
                        totalReceipts += receipts.Count;
                        if (totalReceipts > SyncConstants.MaxRevocationReceiptInventoryEntries)
                            throw new InvalidOperationException("The revocation receipt inventory safety limit was reached.");
                        userInventory.Receipts.AddRange(receipts);
                    }
                }
            }

            // Empty user entries are intentional: they authorize the peer's first operation for
            // this route and let the remote side request it without disclosing unrelated users.
            result.Users.Add(userInventory);
        }

        return result;
    }


    public async Task<UserControlOperationInventoryExchangeReply> BuildInventoryReplyAsync(
        Guid peerDeviceId,
        UserControlOperationInventoryExchangeRequest remoteInventory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteInventory);
        var local = await BuildInventoryAsync(peerDeviceId, ct);
        ValidateInventory(remoteInventory, local);

        ValidateReceiptBatchLimits(remoteInventory.RequestedReceipts.Count, remoteInventory.ReceiptResponses);
        if (_receipts is not null && remoteInventory.ReceiptResponses.Count > 0)
        {
            await _receipts.StoreAsync(remoteInventory.ReceiptResponses.Select(b => b.ToByteArray()), AllowedOperations(local), ct);
            local = await BuildInventoryAsync(peerDeviceId, ct);
        }
        else if (_receipts is null && (remoteInventory.RequestedReceipts.Count > 0 || remoteInventory.ReceiptResponses.Count > 0))
            throw new InvalidOperationException("Revocation receipt exchange is unavailable.");

        if (_receipts is not null) await _receipts.ObservePeerInventoryAsync(peerDeviceId, remoteInventory.Users, ct);
        var reply = new UserControlOperationInventoryExchangeReply();
        reply.Users.AddRange(local.Users);
        reply.RequestedOperations.AddRange(FindMissingOperations(local, remoteInventory.Users));
        if (_receipts is not null)
        {
            reply.RequestedReceipts.AddRange(FindMissingReceipts(local.Users, remoteInventory.Users));
            reply.ReceiptResponses.AddRange((await _receipts.BuildRequestedAsync(
                remoteInventory.RequestedReceipts, AllowedOperations(local), ct)).Select(b => ByteString.CopyFrom(b)));
        }
        return reply;
    }

    public async Task<UserControlOperationInventoryExchangeRequest?> BuildReceiptFollowUpAsync(
        Guid peerDeviceId, UserControlOperationInventoryExchangeReply remote, CancellationToken ct = default)
    {
        if (_receipts is null) return null;
        var local = await BuildInventoryAsync(peerDeviceId, ct);
        var remoteInventory = new UserControlOperationInventoryExchangeRequest();
        remoteInventory.Users.AddRange(remote.Users);
        ValidateInventory(remoteInventory, local);
        ValidateReceiptBatchLimits(remote.RequestedReceipts.Count, remote.ReceiptResponses);
        // Responses are accepted separately against the exact request that caused them.
        var missing = FindMissingReceipts(local.Users, remote.Users);
        if (missing.Count == 0 && remote.RequestedReceipts.Count == 0) return null;
        local.RequestedReceipts.AddRange(missing);
        local.ReceiptResponses.AddRange((await _receipts.BuildRequestedAsync(
            remote.RequestedReceipts, AllowedOperations(local), ct)).Select(b => ByteString.CopyFrom(b)));
        return local;
    }

    public async Task AcceptReceiptReplyAsync(Guid peerDeviceId,
        UserControlOperationInventoryExchangeRequest request, UserControlOperationInventoryExchangeReply reply,
        CancellationToken ct = default)
    {
        ValidateReceiptBatchLimits(reply.RequestedReceipts.Count, reply.ReceiptResponses);
        var expected = request.RequestedReceipts.ToDictionary(RevocationReceiptService.Key);
        var seen = new HashSet<(Guid, Guid, Guid, Guid, int, string)>();
        foreach (var payload in reply.ReceiptResponses)
        {
            var receipt = RevocationDeliveryReceiptUtil.Deserialize(payload.ToByteArray());
            var entry = RevocationReceiptService.ToInventory(receipt);
            var key = RevocationReceiptService.Key(entry);
            if (!seen.Add(key) || !expected.TryGetValue(key, out var wanted) ||
                !Hashing.Verify(entry.ReceiptHash.ToByteArray(), wanted.ReceiptHash.ToByteArray()))
                throw new InvalidDataException("A receipt response does not match an exact outstanding request.");
        }
        if (seen.Count != expected.Count)
            throw new SyncStateUnavailableException("Not all requested receipts were returned; refresh inventory.");
        var local = await BuildInventoryAsync(peerDeviceId, ct);
        var remoteInventory = new UserControlOperationInventoryExchangeRequest();
        remoteInventory.Users.AddRange(reply.Users);
        ValidateInventory(remoteInventory, local);
        if (_receipts is not null && reply.ReceiptResponses.Count > 0)
        {
            await _receipts.StoreAsync(reply.ReceiptResponses.Select(b => b.ToByteArray()), AllowedOperations(local), ct);
        }
        if (_receipts is not null)
            await _receipts.ObservePeerInventoryAsync(peerDeviceId, reply.Users, ct);
    }

    public static IReadOnlyList<RevocationReceiptInventoryEntry> FindMissingReceipts(
        IEnumerable<UserControlOperationUserInventory> localUsers, IEnumerable<UserControlOperationUserInventory> remoteUsers)
    {
        var local = localUsers.ToArray();
        var operations = local.SelectMany(u => u.Operations.Select(o => (Id: Guid.Parse(o.OperationId), User: Guid.Parse(u.UserId))))
            .ToDictionary(o => o.Id, o => o.User);
        var known = local.SelectMany(u => u.Receipts).ToDictionary(RevocationReceiptService.Key);
        var seen = new HashSet<(Guid, Guid, Guid, Guid, int, string)>();
        var result = new List<RevocationReceiptInventoryEntry>();
        var total = 0;
        foreach (var user in remoteUsers)
        {
            if (!Guid.TryParse(user.UserId, out var userId) || userId == Guid.Empty)
                throw new InvalidDataException("The receipt inventory user identity is invalid.");
            foreach (var receipt in user.Receipts.OrderBy(r => r.Kind == 3))
            {
                if (++total > SyncConstants.MaxRevocationReceiptInventoryEntries)
                    throw new InvalidDataException("The remote receipt inventory is too large.");
                var key = RevocationReceiptService.Key(receipt);
                if (!seen.Add(key)) throw new InvalidDataException("Duplicate receipt inventory identity.");
                // Receive/apply the revocation first. A receipt never substitutes for the operation.
                if (!operations.TryGetValue(key.Item1, out var operationUser)) continue;
                if (operationUser != userId) throw new InvalidDataException("Cross-user receipt inventory replay.");
                if (known.TryGetValue(key, out var existing))
                {
                    if (!Hashing.Verify(existing.ReceiptHash.ToByteArray(), receipt.ReceiptHash.ToByteArray()))
                        throw new InvalidDataException("Conflicting revocation receipt hash.");
                    continue;
                }
                if (receipt.Kind == 3 && !local.SelectMany(u => u.Receipts).Concat(result).Any(r =>
                        r.Kind == (int)RevocationReceiptKind.Applied && Hashing.Verify(r.ReceiptHash.ToByteArray(), receipt.AttestedReceiptHash.ToByteArray())))
                    continue;
                if (result.Count < SyncConstants.MaxRevocationReceiptsPerCall) result.Add(receipt.Clone());
            }
        }
        return result;
    }

    private static HashSet<Guid> AllowedOperations(UserControlOperationInventoryExchangeRequest inventory) =>
        inventory.Users.SelectMany(u => u.Operations)
            .Where(o => !o.Quarantined && (o.OperationType == (int)UserControlOperationType.DeviceRemoval || o.OperationType == (int)UserControlOperationType.AccountDeletion))
            .Select(o => Guid.Parse(o.OperationId)).ToHashSet();

    private static void ValidateReceiptBatchLimits(int requestCount, IEnumerable<ByteString> payloads)
    {
        if (requestCount > SyncConstants.MaxRevocationReceiptsPerCall)
            throw new InvalidDataException("Too many revocation receipt requests.");
        var count = 0;
        foreach (var payload in payloads)
            if (++count > SyncConstants.MaxRevocationReceiptsPerCall || payload.Length == 0 || payload.Length > SyncConstants.MaxRevocationReceiptBytes)
                throw new InvalidDataException("The revocation receipt response batch is too large or malformed.");
    }

    public IReadOnlyList<UserControlOperationRequest> FindMissingOperations(
        UserControlOperationInventoryExchangeRequest localInventory,
        IEnumerable<UserControlOperationUserInventory> remoteInventory)
    {
        ArgumentNullException.ThrowIfNull(localInventory);
        ArgumentNullException.ThrowIfNull(remoteInventory);

        var localById = localInventory.Users
            .SelectMany(user => user.Operations.Select(operation => (UserId: ParseGuid(user.UserId, "user"), Operation: operation)))
            .ToDictionary(item => ParseGuid(item.Operation.OperationId, "operation"));
        var localBySequence = localInventory.Users
            .SelectMany(user => user.Operations.Select(operation => (
                UserId: ParseGuid(user.UserId, "user"),
                OriginDeviceId: ParseGuid(operation.OriginDeviceId, "origin device"),
                OriginInstanceId: ParseGuid(operation.OriginInstanceId, "origin instance"),
                operation.OriginSequence,
                Operation: operation)))
            .ToDictionary(item => (item.UserId, item.OriginDeviceId, item.OriginInstanceId, item.OriginSequence));

        var requests = new List<UserControlOperationRequest>();
        var seenRemoteIds = new HashSet<Guid>();
        var seenRemoteSequences = new HashSet<(Guid, Guid, Guid, long)>();
        foreach (var user in remoteInventory)
        {
            var userId = ParseGuid(user.UserId, "user");
            foreach (var operation in user.Operations)
            {
                ValidateInventoryEntry(operation);
                var operationId = ParseGuid(operation.OperationId, "operation");
                var sequenceKey = (
                    userId,
                    ParseGuid(operation.OriginDeviceId, "origin device"),
                    ParseGuid(operation.OriginInstanceId, "origin instance"),
                    operation.OriginSequence);
                if (!seenRemoteIds.Add(operationId) || !seenRemoteSequences.Add(sequenceKey))
                    throw new InvalidDataException("The remote control-operation inventory contains duplicates.");

                if (localById.TryGetValue(operationId, out var localExact))
                {
                    if (localExact.UserId != userId || !CryptographicOperations.FixedTimeEquals(
                            localExact.Operation.OperationHash.ToByteArray(),
                            operation.OperationHash.ToByteArray()))
                    {
                        throw new InvalidDataException("The same control-operation id is advertised with a conflicting hash.");
                    }
                    continue;
                }

                if (localBySequence.TryGetValue(sequenceKey, out var localSequence) &&
                    !CryptographicOperations.FixedTimeEquals(
                        localSequence.Operation.OperationHash.ToByteArray(),
                        operation.OperationHash.ToByteArray()))
                {
                    throw new InvalidDataException("The same original-author control sequence is advertised with incompatible content.");
                }

                if (operation.Quarantined)
                    continue;
                if (requests.Count >= SyncConstants.MaxUserControlRequestsPerCall)
                    throw new InvalidDataException("Too many control operations are missing in one exchange.");

                requests.Add(ToRequest(userId, operation));
            }
        }

        // The inventory is grouped by origin installation, not by causal order. A later
        // device addition can be signed by a newly enrolled device, whose authorization
        // only exists after earlier membership transitions have been applied. Relay the
        // missing control operations in epoch order rather than GUID order so a peer
        // catching up across multiple enrollments can validate each author in turn.
        return OrderRequestsByControlEpoch(requests).ToArray();
    }

    private static IOrderedEnumerable<UserControlOperationRequest> OrderRequestsByControlEpoch(
        IEnumerable<UserControlOperationRequest> requests) =>
        requests.OrderBy(request => request.PreviousMembershipEpoch)
            .ThenBy(request => request.PreviousKeyEpoch)
            .ThenBy(request => request.UserId, StringComparer.Ordinal)
            .ThenBy(request => request.OriginDeviceId, StringComparer.Ordinal)
            .ThenBy(request => request.OriginInstanceId, StringComparer.Ordinal)
            .ThenBy(request => request.OriginSequence)
            .ThenBy(request => request.OperationId, StringComparer.Ordinal);

    public async Task<IReadOnlyList<NetworkDelta>> BuildRequestedOperationDeltasAsync(
        Guid peerDeviceId,
        IEnumerable<UserControlOperationRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var peer = await _devices.GetByIdWithUserDevicesAsync(peerDeviceId, ct)
            ?? throw new UnauthorizedAccessException("The control-operation relay peer is unknown.");
        if (!peer.IsTrusted || peer.IsBlocked)
            throw new UnauthorizedAccessException("The control-operation relay peer is not authorized.");

        // Defend the sender too: callers may supply a request batch in arbitrary order.
        // The encrypted delta timestamps follow this order when the transport sends it.
        var list = OrderRequestsByControlEpoch(requests).ToList();
        if (list.Count > SyncConstants.MaxUserControlRequestsPerCall)
            throw new InvalidDataException("Too many control operations were requested.");

        var seenIds = new HashSet<Guid>();
        var results = new List<NetworkDelta>(list.Count);
        long totalBytes = 0;
        foreach (var request in list)
        {
            ValidateRequest(request);
            var operationId = ParseGuid(request.OperationId, "operation");
            var userId = ParseGuid(request.UserId, "user");
            if (!seenIds.Add(operationId))
                throw new InvalidDataException("The same control operation was requested more than once.");

            var row = await _operations.GetByIdAsync(operationId, ct);
            // An operation ID can be guessed or learned out of band. Treat missing and
            // inaccessible operations alike, including IDs paired with the wrong account.
            if (row is null || row.UserId != userId ||
                row.Status is UserControlOperationStatus.Rejected or UserControlOperationStatus.Quarantined ||
                (!await _routes.IsEligibleAsync(userId, peerDeviceId, ct) &&
                 !await CanRelayHistoricallyAsync(row, peerDeviceId, ct)))
            {
                throw new SyncStateUnavailableException("The requested control operation is unavailable for this peer.");
            }
            if (!RequestMatchesRow(request, row))
                throw new InvalidDataException("The requested control-operation identity or hash does not match durable storage.");

            var delta = await _deltaBuilder.BuildUserControlOperationRelayAsync(row, peer, ct);
            totalBytes += delta.Payload.Length;
            if (totalBytes > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                throw new InvalidDataException("The requested control-operation relay batch is too large.");
            results.Add(delta);
        }

        return results;
    }

    private async Task<bool> CanRelayHistoricallyAsync(UserControlOperation row, Guid peerDeviceId, CancellationToken ct)
    {
        if (_membershipHistory is null ||
            !await _membershipHistory.HasHistoricalAuthorizationAsync(row.UserId, peerDeviceId, ct))
            return false;
        if (row.Status != UserControlOperationStatus.Applied)
            return false;
        if (row.OperationType == UserControlOperationType.AccountDeletion) return true;
        var operations = await _operations.ListForUserAsync(row.UserId, ct);
        var bound = GetHistoricalRelayBound(operations, peerDeviceId);
        return row.ResultingMembershipEpoch <= bound.Membership && row.ResultingKeyEpoch <= bound.Key;
    }

    private (long Membership, long Key) GetHistoricalRelayBound(IEnumerable<UserControlOperation> rows, Guid peerDeviceId)
    {
        var operations = rows.ToArray();
        var revocations = operations.Where(o => o.Status == UserControlOperationStatus.Applied &&
            o.OperationType is UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion).ToArray();
        if (revocations.Length == 0) return (0, 0);
        var memberBound = revocations.Max(o => o.ResultingMembershipEpoch);
        var cutoff = GetHistoricalRevocationCutoff(operations, peerDeviceId);
        var keyBound = revocations.Max(o => o.ResultingKeyEpoch);
        if (cutoff > 0)
        {
            memberBound = Math.Min(memberBound, cutoff);
            keyBound = Math.Min(keyBound, revocations.Where(o => o.OperationType == UserControlOperationType.DeviceRemoval &&
                o.ResultingMembershipEpoch == cutoff).Min(o => o.PreviousKeyEpoch));
        }
        return (memberBound, keyBound);
    }

    private long GetHistoricalRevocationCutoff(IEnumerable<UserControlOperation> operations, Guid peerDeviceId)
    {
        var cutoff = long.MaxValue;
        foreach (var operation in operations.Where(operation =>
                     operation.OperationType == UserControlOperationType.DeviceRemoval &&
                     operation.Status == UserControlOperationStatus.Applied))
        {
            var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
            var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
            if (removal.RemovedDeviceId == peerDeviceId || removal.RemovedDeviceId == _identity.LocalDeviceId)
                cutoff = Math.Min(cutoff, operation.ResultingMembershipEpoch);
        }

        return cutoff == long.MaxValue ? 0 : cutoff;
    }

    private async Task ValidatePeerAsync(Guid peerDeviceId, CancellationToken ct)
    {
        if (peerDeviceId == Guid.Empty)
            throw new ArgumentException("Peer device id is invalid.", nameof(peerDeviceId));
        var peer = await _devices.GetByIdWithUserDevicesAsync(peerDeviceId, ct)
            ?? throw new UnauthorizedAccessException("The control-operation inventory peer is unknown.");
        if (!peer.IsTrusted || peer.IsBlocked)
            throw new UnauthorizedAccessException("The control-operation inventory peer is not authorized.");
    }

    private void ValidateInventory(
        UserControlOperationInventoryExchangeRequest remote,
        UserControlOperationInventoryExchangeRequest local)
    {
        if (remote.Users.Count > SyncConstants.MaxUserControlInventoryUsers)
            throw new InvalidDataException("The remote control-operation inventory contains too many users.");
        var allowedUsers = local.Users.Select(user => ParseGuid(user.UserId, "user")).ToHashSet();
        var seenUsers = new HashSet<Guid>();
        var count = 0;
        var receiptCount = 0;
        var receiptKeys = new HashSet<(Guid, Guid, Guid, Guid, int, string)>();
        var seenOperations = new HashSet<Guid>();
        foreach (var user in remote.Users)
        {
            var userId = ParseGuid(user.UserId, "user");
            if (!seenUsers.Add(userId))
                throw new InvalidDataException("The remote control-operation inventory contains duplicate users.");
            if (!allowedUsers.Contains(userId))
                throw new UnauthorizedAccessException("The remote control-operation inventory contains an unauthorized user.");
            foreach (var operation in user.Operations)
            {
                count++;
                if (count > SyncConstants.MaxUserControlInventoryEntries)
                    throw new InvalidDataException("The remote control-operation inventory contains too many entries.");
                ValidateInventoryEntry(operation);
                if (!seenOperations.Add(ParseGuid(operation.OperationId, "operation")))
                    throw new InvalidDataException("The remote control-operation inventory contains duplicate operations.");
            }
            var operationIds = user.Operations.Select(o => ParseGuid(o.OperationId, "operation")).ToHashSet();
            foreach (var receipt in user.Receipts)
            {
                if (++receiptCount > SyncConstants.MaxRevocationReceiptInventoryEntries)
                    throw new InvalidDataException("The remote revocation receipt inventory is too large.");
                var key = RevocationReceiptService.Key(receipt);
                if (!operationIds.Contains(key.Item1) || !receiptKeys.Add(key))
                    throw new InvalidDataException("A receipt inventory identity is duplicated or not bound to its user's operation inventory.");
            }
        }
        // Validate immutable ID/hash and original sequence conflicts before receipt imports or
        // peer-knowledge writes. These checks must apply to both requests and replies.
        _ = FindMissingOperations(local, remote.Users);
    }

    private void ValidateInventoryEntry(UserControlOperationInventoryEntry entry)
    {
        _ = ParseGuid(entry.OperationId, "operation");
        _ = ParseGuid(entry.OriginDeviceId, "origin device");
        _ = ParseGuid(entry.OriginInstanceId, "origin instance");
        if (entry.OriginSequence <= 0 || !Enum.IsDefined((UserControlOperationType)entry.OperationType))
            throw new InvalidDataException("The control-operation inventory sequence or type is invalid.");
        if (entry.OperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The control-operation inventory hash is invalid.");
        if (entry.ConflictingOperationHash.Length is not (0 or SyncConstants.SyncDeltaPayloadHashBytes))
            throw new InvalidDataException("The control-operation conflicting hash is invalid.");
        if (!entry.Quarantined && entry.ConflictingOperationHash.Length != 0)
            throw new InvalidDataException("A non-quarantined operation cannot advertise a conflicting hash.");
    }

    private void ValidateRequest(UserControlOperationRequest request)
    {
        _ = ParseGuid(request.OperationId, "operation");
        _ = ParseGuid(request.UserId, "user");
        _ = ParseGuid(request.OriginDeviceId, "origin device");
        _ = ParseGuid(request.OriginInstanceId, "origin instance");
        if (request.OriginSequence <= 0 || !Enum.IsDefined((UserControlOperationType)request.OperationType))
            throw new InvalidDataException("The control-operation request sequence or type is invalid.");
        if (request.ExpectedOperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The control-operation request hash is invalid.");
    }

    private UserControlOperationInventoryEntry ToInventoryEntry(UserControlOperation row) =>
        new()
        {
            OperationId = row.OperationId.ToString("N"),
            OriginDeviceId = row.OriginDeviceId.ToString("N"),
            OriginInstanceId = row.OriginInstanceId.ToString("N"),
            OriginSequence = row.OriginSequence,
            OperationType = (int)row.OperationType,
            PreviousKeyEpoch = row.PreviousKeyEpoch,
            ResultingKeyEpoch = row.ResultingKeyEpoch,
            PreviousMembershipEpoch = row.PreviousMembershipEpoch,
            ResultingMembershipEpoch = row.ResultingMembershipEpoch,
            OperationHash = ByteString.CopyFrom(row.OperationHash),
            Quarantined = row.Status == UserControlOperationStatus.Quarantined,
            ConflictingOperationHash = ByteString.CopyFrom(
                row.Status == UserControlOperationStatus.Quarantined
                    ? row.ConflictingOperationHash ?? []
                    : [])
        };

    private UserControlOperationRequest ToRequest(Guid userId, UserControlOperationInventoryEntry entry) =>
        new()
        {
            OperationId = entry.OperationId,
            UserId = userId.ToString("N"),
            OriginDeviceId = entry.OriginDeviceId,
            OriginInstanceId = entry.OriginInstanceId,
            OriginSequence = entry.OriginSequence,
            OperationType = entry.OperationType,
            PreviousKeyEpoch = entry.PreviousKeyEpoch,
            ResultingKeyEpoch = entry.ResultingKeyEpoch,
            PreviousMembershipEpoch = entry.PreviousMembershipEpoch,
            ResultingMembershipEpoch = entry.ResultingMembershipEpoch,
            ExpectedOperationHash = entry.OperationHash
        };

    private bool RequestMatchesRow(UserControlOperationRequest request, UserControlOperation row) =>
        row.OperationId == ParseGuid(request.OperationId, "operation") &&
        row.UserId == ParseGuid(request.UserId, "user") &&
        row.OriginDeviceId == ParseGuid(request.OriginDeviceId, "origin device") &&
        row.OriginInstanceId == ParseGuid(request.OriginInstanceId, "origin instance") &&
        row.OriginSequence == request.OriginSequence &&
        (int)row.OperationType == request.OperationType &&
        row.PreviousKeyEpoch == request.PreviousKeyEpoch &&
        row.ResultingKeyEpoch == request.ResultingKeyEpoch &&
        row.PreviousMembershipEpoch == request.PreviousMembershipEpoch &&
        row.ResultingMembershipEpoch == request.ResultingMembershipEpoch &&
        CryptographicOperations.FixedTimeEquals(row.OperationHash, request.ExpectedOperationHash.ToByteArray());

    private void EnsureRowMatchesEnvelope(UserControlOperation row, UserControlOperationEnvelope envelope)
    {
        if (row.OperationId != envelope.OperationId || row.UserId != envelope.UserId ||
            row.OriginDeviceId != envelope.OriginDeviceId || row.OriginInstanceId != envelope.OriginInstanceId ||
            row.OriginSequence != envelope.OriginSequence || row.OperationType != envelope.OperationType ||
            !Hashing.Verify(row.OperationHash, envelope.OperationHash))
        {
            throw new InvalidDataException("A stored control-operation row does not match its immutable envelope.");
        }
    }

    private Guid ParseGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException($"The {fieldName} id is invalid.");
        return parsed;
    }
}
