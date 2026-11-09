# Synchronization

Enrolled devices reconcile encrypted state over the local network. They can relay authenticated snapshots while locked and apply their contents once they have the user key. Signed [lifecycle operations](ACCOUNT_LIFECYCLE.md) carry authority for device membership, key changes, and account deletion. Ordinary snapshots carry vault data.

## Discovery and transport

[Local discovery](../../Common/Backend/Services/Hosted/LocalDiscoveryHostedService.cs) sends UDP multicast queries and receives unicast responses. [Sync constants](../../Common/Backend/Constants/SyncConstants.cs) and [shared port defaults](../../Common/Contracts/Sync/SyncProtocolDefaults.cs) define the current versions, addresses, bounds, and time windows. The [packet codec](../../Common/Backend/Sync/Discovery/Protocol/LocalDiscoveryPacketCodec.cs) defines the wire fields.

- **Regular sync.** Devices sign queries and responses with Ed25519. A response matches an outstanding nonce, targets the requester, and binds the responder's device identity, address, and TLS fingerprint. The advertised responder address must match the UDP source. Blocked or untrusted devices cannot establish a route through discovery.
- **Enrollment.** Queries and responses use HMAC-SHA256 with the enrollment secret. The response binds the session, device and installation identities, device type, public keys, TLS fingerprint, and responder address. Discovery requires an active interactive enrollment session.
- **Trust boundary.** UDP packets are authenticated and can expose network metadata. Discovery yields candidate addresses. The TCP/TLS exchange authenticates the actual peer and applies device or enrollment authorization before accepting application data. A relayed endpoint hint can help locate a peer, whose identity still has to pass the same connection checks.

## Snapshot exchange

The [TCP protocol handler](../../Common/Backend/Sync/Tcp/SyncPeerProtocolHandler.cs) handles the transport peer. The [snapshot inbox](../../Common/Backend/Services/UserSnapshotInboxService.cs) separately verifies the immutable snapshot's original author using historical authorization and removal cutoffs. A pre-removal revision can still be accepted within the cutoff. Relays retain the original signed envelope.

[Anti-entropy](../../Common/Backend/Services/UserSnapshotAntiEntropyService.cs) compares bounded inventories and requests exact missing identities and hashes. The protocol distinguishes a known revision, an envelope retained for transfer, and a revision incorporated into canonical state. The receiver returns a receipt after durable storage. A pending receipt is compatible with a locked receiver that has yet to merge the contents.

When a user key is available, [the merge coordinator](../../Common/Backend/Services/UserSnapshotMergeCoordinator.cs) verifies and merges eligible snapshots and can publish a new local revision. In an ordinary merge, the publisher can advertise that its publication is equivalent to a specific snapshot authored by the recipient. The recipient can skip the return transfer while its matching authored snapshot is still retained and merged. Fork detection and acquisition of signed coverage receipts take precedence over this optimization.

A repeated revision with the same hash is idempotent. Different hashes for the same origin, key epoch, and revision are evidence of a fork. Control operations have their own inventory and are delivered ahead of dependent snapshots. Missing control predecessors remain pending until the necessary signed history arrives. See [fault isolation](RECOVERY.md#fault-isolation).

## Deterministic item merge

Each independently mergeable item and item tombstone carries a `SyncVersionStamp`. The comparison order is UTC milliseconds, logical counter, device ID, and installation origin ID. The [persistent clock](../../Common/Backend/Services/SyncVersionClockService.cs) advances from local writes and observed remote stamps, so restart or clock rollback does not make a later local mutation sort before observed state.

For each item ID, the highest valid version wins across live and deleted values. Equal versions with different authenticated content are an integrity conflict. General profile data is one atomic item, including username metadata. Encrypted device presentation data has item versions, while signed lifecycle operations decide membership. Enrollment and key rotation carry forward existing logical versions.

The merge orders persisted collections deterministically. Concurrent writes to different IDs can produce duplicate display names even when local creation checks for uniqueness. Reads filter references to deleted tags and resolve device display names without generating a logical edit.

## Causal tombstone collection

A tombstone's item version orders the deletion against edits. Its causal reference identifies the first signed snapshot expected to contain the deletion, including the origin, key epoch, membership epoch, and revision.

The [collector](../../Common/Backend/Services/UserTombstoneGarbageCollector.cs) compacts the deletion only when local merged knowledge covers that reference and each installation authorized at the deletion is accounted for:

- An active installation has an authenticated signed reporting snapshot covering the anchor.
- An installation removed later has complete signed cutoffs, and local merged knowledge covers all accepted revisions named by those cutoffs.
- Installations that joined later begin with the enrollment baseline. A later key epoch counts for an earlier deletion only if the reporting snapshot explicitly covers the earlier namespace.

A signed envelope can serve as a coverage receipt even when its content already merged indirectly. Acquiring the receipt retains evidence without reapplying the content. Missing or conflicting history, pending or quarantined evidence, and exceeded safety limits leave the tombstone in place.

Collection uses the per-user lifecycle lock and a transaction. Compaction preserves surviving logical versions, publishes a new outer snapshot, and queues sync atomically. Account deletion takes precedence over item compaction.
