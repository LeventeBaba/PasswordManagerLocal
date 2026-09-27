using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Builds compact stored/merged inventories and serves exact immutable user snapshots.
/// Relay preserves immutable original-author envelopes. Transport eligibility remains current,
/// while original-author eligibility is verified against durable membership history and cutoffs.
/// </summary>
public sealed class UserSnapshotAntiEntropyService : IUserSnapshotAntiEntropyService
{
    private readonly IUserRepository _users;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ILocalUserDeviceRepository _localUsers;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IDeviceRepository _devices;
    private readonly IOutgoingDeltaBuilderService _deltaBuilder;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;

    public UserSnapshotAntiEntropyService(
        IUserRepository users,
        IUserDeviceRepository userDevices,
        ILocalUserDeviceRepository localUsers,
        IUserRevisionKnowledgeRepository knowledge,
        IUserSyncSnapshotRepository snapshots,
        IDeviceRepository devices,
        IOutgoingDeltaBuilderService deltaBuilder,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationService membershipAuthorization,
        IDeletedUserBarrierRepository? deletionBarriers = null)
    {
        _users = users;
        _userDevices = userDevices;
        _localUsers = localUsers;
        _knowledge = knowledge;
        _snapshots = snapshots;
        _devices = devices;
        _deltaBuilder = deltaBuilder;
        _identity = identity;
        _membershipAuthorization = membershipAuthorization;
        _deletionBarriers = deletionBarriers;
    }

    public async Task<UserSnapshotInventoryExchangeRequest> BuildInventoryAsync(
        Guid peerDeviceId,
        CancellationToken ct = default)
    {
        await ValidatePeerAsync(peerDeviceId, ct);
        var result = new UserSnapshotInventoryExchangeRequest();
        var eligibleUsers = await LoadEligibleUsersAsync(peerDeviceId, ct);

        var totalEntries = 0;
        foreach (var user in eligibleUsers.OrderBy(user => user.UId))
        {
            var userInventory = new UserSnapshotUserInventory
            {
                UserId = user.UId.ToString("N"),
                UserKeyEpoch = user.KeyEpoch,
                MembershipEpoch = user.MembershipEpoch
            };

            var entries = await _knowledge.ListAsync(user.UId, user.KeyEpoch, ct);
            foreach (var item in entries
                         .OrderBy(item => item.OriginDeviceId)
                         .ThenBy(item => item.OriginInstanceId))
            {
                if (item.HighestStoredRevision < 0 || item.HighestMergedRevision < 0)
                    throw new InvalidDataException("User revision knowledge contains an invalid revision.");
                if (item.HighestStoredRevision > 0 &&
                    item.HighestStoredSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
                {
                    throw new InvalidDataException("User revision knowledge is missing its known snapshot hash.");
                }
                if (item.HighestStoredRevision == 0 && item.HighestStoredSnapshotHash.Length != 0)
                    throw new InvalidDataException("User revision knowledge contains a hash without a known revision.");

                // Knowledge records what has ever been received and merged, but an exact snapshot
                // may be deleted after a successful true merge. Advertise "stored" only when the
                // immutable relayable envelope is still retained locally.
                var retained = await _snapshots.GetAsync(
                    user.UId,
                    item.OriginDeviceId,
                    item.OriginInstanceId,
                    item.UserKeyEpoch,
                    ct);

                var storedRevision = 0L;
                byte[] storedHash = [];
                var quarantinedRevision = 0L;
                byte[] quarantinedHash = [];
                byte[] conflictingHash = [];
                if (retained is not null)
                {
                    if (retained.MembershipEpoch <= 0 || retained.MembershipEpoch > user.MembershipEpoch)
                        throw new InvalidDataException("A retained user snapshot has an invalid historical membership epoch.");
                    if (retained.Status is UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt)
                    {
                        if (retained.OriginRevision <= 0 || retained.SnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
                            throw new InvalidDataException("A retained user snapshot has invalid revision metadata.");

                        if (item.HighestStoredRevision < retained.OriginRevision ||
                            (item.HighestStoredRevision == retained.OriginRevision &&
                             !Hashing.Verify(item.HighestStoredSnapshotHash, retained.SnapshotHash)))
                        {
                            throw new InvalidDataException("Retained snapshot is newer than, or conflicts with, durable revision knowledge.");
                        }

                        storedRevision = retained.OriginRevision;
                        storedHash = retained.SnapshotHash;
                    }
                    else if (retained.Status == UserSyncSnapshotStatus.IsolatedFork)
                    {
                        if (retained.OriginRevision <= 0 || retained.SnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
                            throw new InvalidDataException("A quarantined user snapshot has invalid revision metadata.");

                        quarantinedRevision = retained.OriginRevision;
                        quarantinedHash = retained.SnapshotHash;
                        conflictingHash = retained.ConflictingSnapshotHash ?? [];
                        if (conflictingHash.Length is not (0 or SyncConstants.SyncDeltaPayloadHashBytes))
                            throw new InvalidDataException("A quarantined user snapshot has an invalid conflicting hash.");
                    }
                }

                if (storedRevision <= 0 && item.HighestMergedRevision <= 0 &&
                    quarantinedRevision <= 0 && item.HighestStoredRevision <= 0)
                {
                    continue;
                }

                totalEntries++;
                if (totalEntries > SyncConstants.MaxUserSnapshotInventoryEntries)
                    throw new InvalidOperationException("The user snapshot inventory safety limit was reached.");

                userInventory.Revisions.Add(new UserSnapshotRevisionInventory
                {
                    OriginDeviceId = item.OriginDeviceId.ToString("N"),
                    OriginInstanceId = item.OriginInstanceId.ToString("N"),
                    UserKeyEpoch = item.UserKeyEpoch,
                    HighestStoredRevision = storedRevision,
                    HighestStoredSnapshotHash = ByteString.CopyFrom(storedHash),
                    HighestMergedRevision = item.HighestMergedRevision,
                    QuarantinedRevision = quarantinedRevision,
                    QuarantinedSnapshotHash = ByteString.CopyFrom(quarantinedHash),
                    ConflictingSnapshotHash = ByteString.CopyFrom(conflictingHash),
                    KnownSnapshotRevision = item.HighestStoredRevision,
                    KnownSnapshotHash = ByteString.CopyFrom(item.HighestStoredSnapshotHash),
                    RetainedMembershipEpoch = storedRevision > 0 ? retained!.MembershipEpoch : 0
                });
            }

            result.Users.Add(userInventory);
        }

        return result;
    }

    public async Task<UserSnapshotInventoryExchangeReply> BuildInventoryReplyAsync(
        Guid peerDeviceId,
        UserSnapshotInventoryExchangeRequest remoteInventory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteInventory);
        var localInventory = await BuildInventoryAsync(peerDeviceId, ct);
        ValidateRemoteInventory(remoteInventory, localInventory);

        var reply = new UserSnapshotInventoryExchangeReply();
        reply.Users.AddRange(localInventory.Users);
        reply.RequestedSnapshots.AddRange(FindMissingSnapshots(localInventory, remoteInventory.Users));
        return reply;
    }

    public IReadOnlyList<UserSnapshotRequest> FindMissingSnapshots(
        UserSnapshotInventoryExchangeRequest localInventory,
        IEnumerable<UserSnapshotUserInventory> remoteInventory)
    {
        ArgumentNullException.ThrowIfNull(localInventory);
        ArgumentNullException.ThrowIfNull(remoteInventory);

        var remoteUsers = remoteInventory.ToList();
        var remoteRequest = new UserSnapshotInventoryExchangeRequest();
        remoteRequest.Users.AddRange(remoteUsers);
        ValidateRemoteInventory(remoteRequest, localInventory);

        var localUsers = localInventory.Users.ToDictionary(ParseUserId);
        var requests = new List<UserSnapshotRequest>();
        var seen = new HashSet<(Guid UserId, Guid OriginDeviceId, Guid OriginInstanceId, long KeyEpoch, long Revision)>();

        foreach (var remoteUser in remoteUsers.OrderBy(ParseUserId))
        {
            var userId = ParseUserId(remoteUser);
            if (!localUsers.TryGetValue(userId, out var localUser) ||
                localUser.UserKeyEpoch != remoteUser.UserKeyEpoch ||
                localUser.MembershipEpoch != remoteUser.MembershipEpoch)
            {
                continue;
            }

            var localEntries = localUser.Revisions.ToDictionary(ParseRevisionKey);
            foreach (var remoteEntry in remoteUser.Revisions
                         .OrderBy(entry => ParseGuid(entry.OriginDeviceId, "origin device"))
                         .ThenBy(entry => ParseGuid(entry.OriginInstanceId, "origin instance")))
            {
                ValidateRevision(remoteEntry, remoteUser.UserKeyEpoch, remoteUser.MembershipEpoch);
                if (remoteEntry.HighestStoredRevision <= 0)
                    continue;

                var key = ParseRevisionKey(remoteEntry);
                localEntries.TryGetValue(key, out var localEntry);
                if (localEntry is not null && localEntry.QuarantinedRevision > 0)
                    continue;
                var sameKnownBadRevision = localEntry is not null &&
                                           localEntry.HighestStoredRevision == 0 &&
                                           localEntry.HighestMergedRevision < localEntry.KnownSnapshotRevision &&
                                           localEntry.KnownSnapshotRevision == remoteEntry.HighestStoredRevision &&
                                           localEntry.KnownSnapshotHash.Length == SyncConstants.SyncDeltaPayloadHashBytes &&
                                           CryptographicOperations.FixedTimeEquals(
                                               localEntry.KnownSnapshotHash.ToByteArray(),
                                               remoteEntry.HighestStoredSnapshotHash.ToByteArray());
                if (sameKnownBadRevision)
                    continue;

                var sameKnownRevisionFork = localEntry is not null &&
                                            localEntry.KnownSnapshotRevision == remoteEntry.HighestStoredRevision &&
                                            localEntry.KnownSnapshotHash.Length == SyncConstants.SyncDeltaPayloadHashBytes &&
                                            !CryptographicOperations.FixedTimeEquals(
                                                localEntry.KnownSnapshotHash.ToByteArray(),
                                                remoteEntry.HighestStoredSnapshotHash.ToByteArray());
                // A newer retained envelope from the same immutable origin/key namespace
                // dominates an older retained envelope because published coverage is monotonic.
                // Durable "known" revision metadata is deliberately not enough here: the exact
                // signed envelope may no longer be retained locally and therefore cannot serve as
                // authenticated causal-GC receipt evidence.
                if (localEntry is not null &&
                    localEntry.HighestStoredRevision > remoteEntry.HighestStoredRevision)
                    continue;

                var needsSnapshot = sameKnownRevisionFork ||
                                    localEntry is null ||
                                    localEntry.HighestStoredRevision < remoteEntry.HighestStoredRevision ||
                                    (localEntry.HighestStoredRevision == remoteEntry.HighestStoredRevision &&
                                     !CryptographicOperations.FixedTimeEquals(
                                         localEntry.HighestStoredSnapshotHash.ToByteArray(),
                                         remoteEntry.HighestStoredSnapshotHash.ToByteArray()));
                if (!needsSnapshot)
                    continue;

                var originDeviceId = ParseGuid(remoteEntry.OriginDeviceId, "origin device");
                var originInstanceId = ParseGuid(remoteEntry.OriginInstanceId, "origin instance");
                var requestKey = (userId, originDeviceId, originInstanceId, remoteEntry.UserKeyEpoch, remoteEntry.HighestStoredRevision);
                if (!seen.Add(requestKey))
                    continue;

                requests.Add(new UserSnapshotRequest
                {
                    UserId = userId.ToString("N"),
                    OriginDeviceId = originDeviceId.ToString("N"),
                    OriginInstanceId = originInstanceId.ToString("N"),
                    OriginRevision = remoteEntry.HighestStoredRevision,
                    UserKeyEpoch = remoteEntry.UserKeyEpoch,
                    MembershipEpoch = remoteEntry.RetainedMembershipEpoch,
                    ExpectedSnapshotHash = remoteEntry.HighestStoredSnapshotHash
                });

                if (requests.Count >= SyncConstants.MaxUserSnapshotRequestsPerCall)
                    return requests;
            }
        }

        return requests;
    }

    public async Task<IReadOnlyList<NetworkDelta>> BuildRequestedSnapshotDeltasAsync(
        Guid peerDeviceId,
        IEnumerable<UserSnapshotRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var requestList = requests.ToList();
        if (requestList.Count > SyncConstants.MaxUserSnapshotRequestsPerCall)
            throw new InvalidDataException("Too many user snapshot requests were supplied.");

        var peer = await _devices.GetByIdWithUserDevicesAsync(peerDeviceId, ct)
            ?? throw new UnauthorizedAccessException("The requesting peer is unknown.");
        if (!peer.IsTrusted || peer.IsBlocked)
            throw new UnauthorizedAccessException("The requesting peer is not authorized.");

        var results = new List<NetworkDelta>();
        var seen = new HashSet<(Guid UserId, Guid OriginDeviceId, Guid OriginInstanceId, long KeyEpoch, long Revision)>();
        long totalBytes = 0;

        foreach (var request in requestList
                     .OrderBy(request => ParseGuid(request.UserId, "user"))
                     .ThenBy(request => ParseGuid(request.OriginDeviceId, "origin device"))
                     .ThenBy(request => ParseGuid(request.OriginInstanceId, "origin instance"))
                     .ThenBy(request => request.OriginRevision))
        {
            var userId = ParseGuid(request.UserId, "user");
            var originDeviceId = ParseGuid(request.OriginDeviceId, "origin device");
            var originInstanceId = ParseGuid(request.OriginInstanceId, "origin instance");
            ValidateRequest(request);

            if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
                throw new InvalidDataException("The requested account identity is permanently deleted; ordinary snapshots are no longer relayable.");

            var requestKey = (userId, originDeviceId, originInstanceId, request.UserKeyEpoch, request.OriginRevision);
            if (!seen.Add(requestKey))
                throw new InvalidDataException("A duplicate user snapshot request was supplied.");

            if (!await _localUsers.IsSyncOnAsync(userId, ct) ||
                !await _userDevices.HasActiveLinkAsync(userId, peerDeviceId, ct))
            {
                throw new UnauthorizedAccessException("The requesting peer cannot access this user.");
            }

            var user = await _users.GetByIdAsNoTrackingAsync(userId, ct)
                ?? throw new UnauthorizedAccessException("The requested user does not exist locally.");
            if (user.KeyEpoch != request.UserKeyEpoch || request.MembershipEpoch > user.MembershipEpoch)
                throw new InvalidDataException("The requested snapshot epoch is not safely available from canonical state.");

            var snapshot = await _snapshots.GetExactAsync(
                userId,
                originDeviceId,
                originInstanceId,
                request.UserKeyEpoch,
                request.OriginRevision,
                ct) ?? throw new InvalidDataException("The requested exact user snapshot is not stored locally.");

            if (snapshot.Status is not (UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt))
                throw new InvalidDataException("The requested user snapshot is not relayable.");
            if (snapshot.MembershipEpoch != request.MembershipEpoch)
                throw new InvalidDataException("The requested user snapshot membership epoch does not match.");
            if (!Hashing.Verify(snapshot.SnapshotHash, request.ExpectedSnapshotHash.ToByteArray()))
                throw new InvalidDataException("The requested user snapshot hash does not match the stored envelope.");

            var envelope = Deserialize(snapshot);
            await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);

            var delta = await _deltaBuilder.BuildUserSnapshotRelayAsync(snapshot, peer, ct);
            totalBytes += delta.Payload.Length;
            if (totalBytes > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                throw new InvalidDataException("The requested user snapshot relay batch is too large.");

            results.Add(delta);
        }

        return results;
    }

    private async Task ValidatePeerAsync(Guid peerDeviceId, CancellationToken ct)
    {
        if (peerDeviceId == Guid.Empty)
            throw new ArgumentException("Peer device id is invalid.", nameof(peerDeviceId));

        var peer = await _devices.GetByIdWithUserDevicesAsync(peerDeviceId, ct)
            ?? throw new UnauthorizedAccessException("The inventory peer is unknown.");
        if (!peer.IsTrusted || peer.IsBlocked)
            throw new UnauthorizedAccessException("The inventory peer is not authorized.");
    }


    private async Task<IReadOnlyList<User>> LoadEligibleUsersAsync(Guid peerDeviceId, CancellationToken ct)
    {
        if (peerDeviceId == Guid.Empty)
            throw new ArgumentException("Peer device id is invalid.", nameof(peerDeviceId));

        var peerLinks = await _userDevices.ListByDeviceAsync(peerDeviceId, ct);
        var localUserIds = (await _localUsers.ListSyncOnUserIdsAsync(ct)).ToHashSet();
        var eligibleIds = peerLinks
            .Where(link => !link.IsDeleted && link.IsSyncOn && localUserIds.Contains(link.UserId))
            .Select(link => link.UserId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (eligibleIds.Length > SyncConstants.MaxUserSnapshotInventoryUsers)
            throw new InvalidOperationException("The user snapshot inventory user limit was reached.");

        return await _users.ListByIdsAsync(eligibleIds, ct);
    }

    private void ValidateRemoteInventory(
        UserSnapshotInventoryExchangeRequest remote,
        UserSnapshotInventoryExchangeRequest local)
    {
        if (remote.Users.Count > SyncConstants.MaxUserSnapshotInventoryUsers)
            throw new InvalidDataException("The remote user snapshot inventory contains too many users.");

        var allowedUsers = local.Users.ToDictionary(ParseUserId);
        var seenUsers = new HashSet<Guid>();
        var entryCount = 0;
        foreach (var user in remote.Users)
        {
            var userId = ParseUserId(user);
            if (!seenUsers.Add(userId))
                throw new InvalidDataException("The remote inventory contains duplicate users.");
            if (!allowedUsers.TryGetValue(userId, out var allowed) ||
                allowed.UserKeyEpoch != user.UserKeyEpoch ||
                allowed.MembershipEpoch != user.MembershipEpoch)
            {
                throw new UnauthorizedAccessException("The remote inventory contains an unauthorized or stale user epoch.");
            }

            var seenEntries = new HashSet<(Guid, Guid, long)>();
            foreach (var entry in user.Revisions)
            {
                entryCount++;
                if (entryCount > SyncConstants.MaxUserSnapshotInventoryEntries)
                    throw new InvalidDataException("The remote user snapshot inventory contains too many revision entries.");
                ValidateRevision(entry, user.UserKeyEpoch, user.MembershipEpoch);
                if (!seenEntries.Add(ParseRevisionKey(entry)))
                    throw new InvalidDataException("The remote inventory contains duplicate origin revision namespaces.");
            }
        }
    }

    private void ValidateRevision(
        UserSnapshotRevisionInventory entry,
        long expectedKeyEpoch,
        long currentMembershipEpoch)
    {
        _ = ParseGuid(entry.OriginDeviceId, "origin device");
        _ = ParseGuid(entry.OriginInstanceId, "origin instance");
        if (entry.UserKeyEpoch <= 0 || entry.UserKeyEpoch != expectedKeyEpoch)
            throw new InvalidDataException("The inventory key epoch is invalid.");
        if (entry.HighestStoredRevision < 0 || entry.HighestMergedRevision < 0)
            throw new InvalidDataException("The inventory revision is invalid.");
        if (entry.HighestStoredRevision > 0 && entry.HighestStoredSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The inventory stored revision hash is invalid.");
        if (entry.HighestStoredRevision == 0 && entry.HighestStoredSnapshotHash.Length != 0)
            throw new InvalidDataException("The inventory contains a hash without a stored revision.");
        if (entry.HighestStoredRevision > 0 &&
            (entry.RetainedMembershipEpoch <= 0 || entry.RetainedMembershipEpoch > currentMembershipEpoch))
        {
            throw new InvalidDataException("The inventory retained snapshot membership epoch is invalid.");
        }
        if (entry.HighestStoredRevision == 0 && entry.RetainedMembershipEpoch != 0)
            throw new InvalidDataException("The inventory contains a retained membership epoch without a stored revision.");
        if (entry.QuarantinedRevision < 0)
            throw new InvalidDataException("The inventory quarantine revision is invalid.");
        if (entry.QuarantinedRevision > 0 && entry.QuarantinedSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The inventory quarantined snapshot hash is invalid.");
        if (entry.QuarantinedRevision == 0 &&
            (entry.QuarantinedSnapshotHash.Length != 0 || entry.ConflictingSnapshotHash.Length != 0))
        {
            throw new InvalidDataException("The inventory contains quarantine hashes without a quarantined revision.");
        }
        if (entry.ConflictingSnapshotHash.Length is not (0 or SyncConstants.SyncDeltaPayloadHashBytes))
            throw new InvalidDataException("The inventory conflicting snapshot hash is invalid.");
        if (entry.HighestStoredRevision > 0 && entry.QuarantinedRevision > 0)
            throw new InvalidDataException("An inventory namespace cannot be both relayable and quarantined.");
        if (entry.KnownSnapshotRevision < 0)
            throw new InvalidDataException("The inventory known snapshot revision is invalid.");
        if (entry.KnownSnapshotRevision > 0 && entry.KnownSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The inventory known snapshot hash is invalid.");
        if (entry.KnownSnapshotRevision == 0 && entry.KnownSnapshotHash.Length != 0)
            throw new InvalidDataException("The inventory contains a known hash without a known revision.");
        if (entry.HighestStoredRevision > entry.KnownSnapshotRevision)
            throw new InvalidDataException("The inventory retained snapshot is newer than durable known revision metadata.");
        if (entry.HighestStoredRevision > 0 &&
            entry.KnownSnapshotRevision == entry.HighestStoredRevision &&
            !CryptographicOperations.FixedTimeEquals(
                entry.KnownSnapshotHash.ToByteArray(),
                entry.HighestStoredSnapshotHash.ToByteArray()))
        {
            throw new InvalidDataException("The inventory retained and known snapshot hashes conflict at the same revision.");
        }
    }

    private void ValidateRequest(UserSnapshotRequest request)
    {
        if (request.OriginRevision <= 0 || request.UserKeyEpoch <= 0 || request.MembershipEpoch <= 0)
            throw new InvalidDataException("The requested user snapshot revision or epoch is invalid.");
        if (request.ExpectedSnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The requested user snapshot hash is invalid.");
    }

    private Device BuildLocalOriginDevice() =>
        new()
        {
            Id = _identity.LocalDeviceId,
            SignPublicKey = _identity.SignPublicKey.ToArray(),
            PublicKey = _identity.AgreementPublicKey.ToArray(),
            TlsCertFingerprint = _identity.FingerprintHex,
            IsTrusted = true,
            IsBlocked = false
        };


    private UserSnapshotEnvelope Deserialize(UserSyncSnapshot snapshot)
    {
        if (snapshot.EnvelopePayload.Length == 0 || snapshot.EnvelopePayload.Length > SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The stored user snapshot envelope size is invalid.");
        var envelope = JsonSerializer.Deserialize(snapshot.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The stored user snapshot envelope is invalid.");
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (!Hashing.Verify(envelope.SnapshotHash, snapshot.SnapshotHash))
            throw new InvalidDataException("The stored user snapshot row hash does not match its immutable envelope.");
        return envelope;
    }

    private Guid ParseUserId(UserSnapshotUserInventory user) => ParseGuid(user.UserId, "user");

    private (Guid OriginDeviceId, Guid OriginInstanceId, long KeyEpoch) ParseRevisionKey(UserSnapshotRevisionInventory entry) =>
        (ParseGuid(entry.OriginDeviceId, "origin device"), ParseGuid(entry.OriginInstanceId, "origin instance"), entry.UserKeyEpoch);

    private Guid ParseGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException($"The {fieldName} id is invalid.");
        return parsed;
    }
}
