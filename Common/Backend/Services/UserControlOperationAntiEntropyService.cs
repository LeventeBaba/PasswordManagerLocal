using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
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

    public UserControlOperationAntiEntropyService(
        IUserControlOperationRepository operations,
        IDeviceRepository devices,
        ISyncRouteRepository routes,
        IOutgoingDeltaBuilderService deltaBuilder,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationRepository? membershipHistory = null)
    {
        _operations = operations;
        _devices = devices;
        _routes = routes;
        _deltaBuilder = deltaBuilder;
        _identity = identity;
        _membershipHistory = membershipHistory;
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
        foreach (var userId in eligibleUserIds.OrderBy(id => id))
        {
            var userInventory = new UserControlOperationUserInventory { UserId = userId.ToString("N") };
            if (rowsByUser.TryGetValue(userId, out var group))
            {
                foreach (var row in group
                             .Where(operation => activeUserIds.Contains(userId) ||
                                                 (operation.OperationType == UserControlOperationType.AccountDeletion &&
                                                  operation.Status == UserControlOperationStatus.Applied))
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

        var reply = new UserControlOperationInventoryExchangeReply();
        reply.Users.AddRange(local.Users);
        reply.RequestedOperations.AddRange(FindMissingOperations(local, remoteInventory.Users));
        return reply;
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
                    if (!CryptographicOperations.FixedTimeEquals(
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

        return requests;
    }

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

        var list = requests.ToList();
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

            var row = await _operations.GetByIdAsync(operationId, ct)
                ?? throw new InvalidDataException("The requested control operation is not retained locally.");
            var routeEligible = await _routes.IsEligibleAsync(userId, peerDeviceId, ct);
            var historicalDeletionEligible = row.OperationType == UserControlOperationType.AccountDeletion &&
                row.Status == UserControlOperationStatus.Applied &&
                _membershipHistory is not null &&
                await _membershipHistory.HasHistoricalAuthorizationAsync(userId, peerDeviceId, ct);
            if (!routeEligible && !historicalDeletionEligible)
                throw new UnauthorizedAccessException("The peer is not authorized for the requested control operation.");
            if (row.UserId != userId || row.Status is UserControlOperationStatus.Rejected or UserControlOperationStatus.Quarantined)
                throw new InvalidDataException("The requested control operation is not relayable.");
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
            }
        }
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
