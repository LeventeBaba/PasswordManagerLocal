# Synchronization

Enrolled installations exchange encrypted vault snapshots and signed account-control operations over the local network. Transport identity, snapshot authorship, membership authority, and knowledge of merged data are verified separately.

Terminal evidence is scoped by profile. It stops that profile's merge and publication before keyed reconstruction or counter advancement; canonical-health success cannot override it. Unrelated profiles retain their own eligibility and discovery. Ordinary corrupt retained evidence is isolated without promoting an unverifiable record into proof of a signed fork. Recovery never starts interactive enrollment or grants membership. Signed removal/deletion and revocation-receipt verification remain under the existing control protocol; a local publication refusal uses `SyncStateUnavailableException` and is not peer misconduct. See [integrity and recovery](RECOVERY.md).

## Discovery and transport

[LocalDiscoveryHostedService](../../Common/Backend/Services/Hosted/LocalDiscoveryHostedService.cs) uses UDP multicast queries and unicast responses. [SyncProtocolDefaults](../../Common/Contracts/Sync/SyncProtocolDefaults.cs) defines TCP port 26688 and discovery UDP port 26689. [SyncConstants](../../Common/Backend/Constants/SyncConstants.cs) defines wire versions, multicast addresses, message bounds, timeouts, and replay windows.

| Mode | Authentication | Admission |
| --- | --- | --- |
| Regular synchronization | Ed25519-signed query/response; response binds the request nonce, recipient, responder identity, address, and TLS fingerprint | Trusted, unblocked peer with applicable account authority |
| Enrollment | HMAC-SHA256 with the enrollment secret; response binds the session, installation identity, keys, device type, address, and TLS fingerprint | Active interactive enrollment session |

The advertised responder address must match the UDP source. Discovery supplies candidate endpoints. TCP/TLS binds the connected peer to its pinned identity before account-specific authorization. Relayed endpoint hints do not establish trust.

## Runtime eligibility

[SyncRuntimeEligibilityRepository](../../Common/Backend/Repositories/SyncRuntimeEligibilityRepository.cs) reads one committed SQLite snapshot. Ordinary synchronization requires at least one profile satisfying all of these conditions:

- The exact local installation has a sync-enabled `LocalUserDevice`.
- A distinct remote `UserDevice` is sync-enabled and not deleted.
- The remote device is trusted, unblocked, and has usable public identities.
- Both installations have active membership authorization for the profile's current membership and key epochs.
- The local origin, keys, and certificate match local authority; the remote signing identity and certificate match its retained device record.
- No applicable removal cutoff, account-deletion barrier, or control-plane conflict excludes the relationship.

Qualifying relationship/device rows pass integrity checks. Eligibility does not decrypt vault payloads or depend on endpoint availability, heartbeat age, or an online flag. An offline eligible peer continues to justify discovery. One qualifying relationship is sufficient across all profiles.

`LocalUserDevice.IsSyncOn` is the local profile preference. `UserDevice.IsSyncOn` is the locally stored peer relationship setting, not a live report of the remote service. `DeviceIdentityService.IsSyncOn` is derived runtime state and is not an eligibility input.

The platform background preference controls autonomous backend ownership. Foreground synchronization and explicit enrollment use interactive ownership independently of that preference.

## Activation and service ownership

[SyncRuntimeService](../../Common/Backend/Services/SyncRuntimeService.cs) serializes startup, reconciliation, cleanup, and shutdown. Its activation reasons are independent:

| `SyncRuntimeReason` | Source |
| --- | --- |
| `NormalProfileSynchronization` | Eligible persisted profile/device relationship |
| `PendingRevocationDelivery` | Actionable signed delivery or custody obligations |
| `ActiveEnrollment` | Explicit interactive enrollment session |
| `ActiveInFlightSynchronization` | Existing worker/frame activity while effective synchronization is enabled |

Enrollment alone does not enable ordinary profile exchange. Control-only sessions remain subject to route authorization and stop before ordinary snapshot exchange when no eligible user route exists. Completing revocation delivery leaves an eligible normal relationship active. Removing the last ordinary relationship does not discard outstanding delivery obligations.

Controlled services start in order: identity warmup, TCP listener, local discovery, network refresh. Cleanup runs in reverse order after worker drain. Interactive presence polling has a separate lifetime.

`SyncRuntimeState` distinguishes `Disabled`, `Starting`, `Running`, `Stopping`, and `Degraded`. `Disabled` means no active synchronization work; `IsPassiveListening` separately reports passive historical ingress. `RequiresProcessRestart` distinguishes unsafe ownership from a retryable degraded state.

## Reconciliation and failure handling

[BackendChangeSet](../../Common/Backend/Persistence/BackendChangeSet.cs) requests reconciliation after a durable commit affecting eligibility, authority, or delivery. Rollbacks do not publish that signal. Heartbeat-only writes and changes to the derived transport switch do not create eligibility feedback loops.

[RevocationDeliveryRuntimeHostedService](../../Common/Backend/Services/Hosted/RevocationDeliveryRuntimeHostedService.cs) consumes the bounded, coalesced activity channel with a 75 ms debounce. Startup performs a fresh authoritative read. Repeated unchanged decisions do not restart healthy services.

Recoverable failures retry after 1, 2, 4, 8, 16, 32, then at most 60 seconds. Relevant external changes can advance reconciliation; successful reconciliation resets the retry count. A 30-second fallback checks process-local task health while active or passive networking exists. Healthy checks perform no periodic SQLite reads or writes. An idle runtime without listeners has no periodic reconciliation timer.

Partial startup is cleaned up before retry. Failed cleanup, unconfirmed worker/socket ownership, corrupt integrity/database evidence, and non-retryable platform failures latch `RequiresProcessRestart`. Reconciliation cannot clear that latch or create replacement network owners. Backend shutdown joins supervision before draining workers, preventing late activity releases from reopening services.

## Passive historical ingress

[PassiveDiscoveryHostedService](../../Common/Backend/Services/Hosted/PassiveDiscoveryHostedService.cs) owns passive TCP/discovery ingress while the backend is alive and retains a matching trusted, unblocked remote historical identity. A local-only genesis does not justify passive sockets.

Passive discovery answers authenticated historical peers without multicast querying or outgoing workers. Passive TCP permits authorized control inventories and relay; ordinary snapshot traffic is unavailable. Certificate, signature, cutoff, frame, and replay checks remain active. Passive ownership does not enable ordinary profile synchronization.

Platform ownership determines whether a backend remains alive to serve passive traffic; see [application architecture](APPLICATION.md#background-ownership-and-health).

## Worker coalescing and network changes

[DeviceSyncTaskService](../../Common/Backend/Services/DeviceSyncTaskService.cs) coalesces same-endpoint discovery while a worker is running. Explicit activation retains a follow-up request. After a successful full reconciliation, an unchanged endpoint can skip discovery-only exchanges for 60 seconds interactively or 3 minutes in background mode, provided no durable ordinary queue work exists and the committed evidence generation is unchanged.

New authority/receipt/debt evidence, queued work, explicit activation, endpoint changes, failure, and runtime/network restart bypass or invalidate that success window. Unchanged unresolved delivery does not continuously defeat the window. A success stamp is process-local and does not establish network-wide convergence.

[SyncNetworkRefreshHostedService](../../Common/Backend/Services/Hosted/SyncNetworkRefreshHostedService.cs) debounces OS notifications and compares effective network signatures under its refresh lock. The baseline advances only after successful rebind. Availability transitions, transient changes/reversions, and changes observed during rebind remain actionable. An unchanged signature avoids restarting workers and listeners.

Active networking retains its execution-profile network poller. Passive ingress retains OS notifications without idle polling. Full shutdown drains pending rebinds before closing passive listeners.

## Snapshot exchange and relay

[SyncPeerProtocolHandler](../../Common/Backend/Sync/Tcp/SyncPeerProtocolHandler.cs) authenticates the transport peer. [UserSnapshotInboxService](../../Common/Backend/Services/UserSnapshotInboxService.cs) separately authenticates the immutable original envelope using historical membership and removal cutoffs. A relay preserves the original signature, identity, revision, and hash.

[UserSnapshotAntiEntropyService](../../Common/Backend/Services/UserSnapshotAntiEntropyService.cs) compares bounded inventories and requests exact missing identities/hashes. Durable storage, retained transferable evidence, and canonical merge knowledge are distinct. A locked installation can acknowledge durable ciphertext storage and relay it without claiming a canonical merge.

[UserSnapshotMergeCoordinator](../../Common/Backend/Services/UserSnapshotMergeCoordinator.cs) verifies and merges eligible snapshots when a key is available, then updates knowledge and publication state. A publication can identify equivalent recipient-authored content to avoid redundant transfer. Fork detection and acquisition of signed coverage evidence take precedence over that optimization.

The same revision/hash is idempotent. Different hashes for the same origin, key epoch, and revision establish a fork. The inbox retains the original competing envelope as bounded evidence alongside the original row. Protocol 17 supports a signed removal carrying the exact pair and authorization ID; [independent canonical proof](RECOVERY.md#user-authorized-device-recovery) is required to initiate that removal and retire the local blocker. Zero cutoffs exclude all publications from that installation, including later relay snapshots reporting its causal coverage. Historical fork evidence is never ordinary transferable/recovery data; authorized enrollment payload 8 retains it with its signed exclusion.

Control operations arrive before dependent ordinary snapshots; missing predecessors remain pending. [Account lifecycle](ACCOUNT_LIFECYCLE.md#revocation-delivery) defines revocation receipt exchange and completion.

## Deterministic merge

Each mergeable item and item tombstone carries `SyncVersionStamp`, ordered by UTC milliseconds, logical counter, device ID, and installation origin ID. [SyncVersionClockService](../../Common/Backend/Services/SyncVersionClockService.cs) persists local advancement and observed remote versions across restart and clock rollback.

The highest valid item version wins across live/deleted values. Equal versions with different authenticated content are integrity conflicts. General profile data, including username metadata, is one atomic item. Encrypted device presentation data has item versions; signed control operations determine membership.

Collection order is deterministic. Concurrent independent IDs can retain duplicate display names despite local uniqueness checks. Reads filter references to deleted tags and resolve presentation names without creating logical edits.

## Causal tombstone collection

A tombstone's item version orders deletion against edits. Its causal reference identifies the first signed snapshot containing that deletion: origin, key epoch, membership epoch, and revision.

[UserTombstoneGarbageCollector](../../Common/Backend/Services/UserTombstoneGarbageCollector.cs) removes a tombstone only when local merged knowledge covers the reference and every installation authorized at deletion is accounted for:

- Active installations have authenticated signed reporting snapshots covering the anchor.
- Later-removed installations have complete signed cutoffs and local merged coverage of every accepted revision named by those cutoffs.
- Later additions start from their enrollment baseline. A later key epoch covers an earlier deletion only through explicit earlier-namespace coverage.

Signed envelopes can serve as coverage receipts after indirect merge. Recovery candidates, isolated corruption, terminal forks, historical fork evidence, active GC-blocking health faults and control conflicts remain account-wide blockers for tombstone collection. Signed fork exclusion can resume ordinary healthy synchronization without relaxing this conservative GC boundary. Ordinary pending snapshots are evaluated per tombstone after their exact row/envelope identity, original signature, historical membership and removal cutoffs have been verified.

A pending revision is ignorable only when all of the following hold:

- The exact signed deletion-anchor publication is still retained as a local publication or merged receipt. A newer reporting snapshot cannot stand in for that exact anchor.
- Local knowledge truly merged the pending revision, and its highest stored revision/hash matches the retained pending envelope exactly. Stored-only knowledge is insufficient.
- The deletion publication's signed coverage includes that pending origin/key/revision and dominates its entire signed coverage vector. The pending membership/key epochs cannot follow the anchor's epochs.
- The existing all-member signed receipt/removal-cutoff stability check succeeds for this particular deletion.

This proves causal ancestry under the cumulative snapshot contract; it does not infer item absence from encrypted ciphertext. A pending snapshot that only reports having observed the deletion could contain a later competing edit and continues to block. Missing anchor, incomplete causal proof, unknown authority, invalid signatures and safety limits retain the affected deletion. Enumeration order does not change the decision. The unresolved payload and its exact signed evidence remain stored even when an independently stable tombstone is collected. Pending evidence is sorted once, and decisions for a shared deletion anchor are reused only within the same lifecycle-owned write transaction. No cached causal proof survives a later mutation.

Collection uses per-user lifecycle ownership and a transaction, preserves surviving logical versions, and atomically publishes and queues the compacted state. Account deletion takes precedence. Evidence compaction uses the same lifecycle coordinator and SQLite writer ownership, retains every snapshot/receipt used by GC, and changes only authenticated duplicate revocation-proof representation; see [storage retention](APPLICATION.md#authenticated-evidence-compaction). Neither maintenance operation schedules the other.
