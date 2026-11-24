# Integrity and recovery

The backend separates structural database failure, damaged local canonical data, invalid snapshot-origin evidence, and conflicting signed authority. Each condition has its own admission and recovery boundary. Fault diagnostics retain identifiers, hashes, affected scopes, and recovery state rather than plaintext vault content or keys. Superseded ordinary origin faults may reduce their free-form diagnostic code after exact authenticated replacement and merged-knowledge verification; IDs, hashes, affected components, categories and recovery identities remain. Unresolved journals and terminal conflicts block that reduction. See [authenticated evidence compaction](APPLICATION.md#authenticated-evidence-compaction).

## Integrity boundaries

| Layer | Verification |
| --- | --- |
| SQLite structure | [AppDatabaseInitializer](../../Common/Backend/Persistence/AppDatabaseInitializer.cs) runs `quick_check` before normal writes; structural failure blocks startup |
| Local canonical state | [UserCanonicalCheckpointUtil](../../Common/Backend/Sync/UserCanonicalCheckpointUtil.cs) verifies the local signed commitment to epochs, lookup/password metadata, integrity hash, and encrypted payloads |
| Encrypted bundle | [UserDataBundleVerificationService](../../Common/Backend/Services/UserDataBundleVerificationService.cs) verifies decryption, nested integrity, component links, item versions, and username metadata |
| Incoming origin | Immutable envelope authentication plus historical membership/key/cutoff checks precede evidence use |
| Publication | [UserSnapshotPublisherService](../../Common/Backend/Services/UserSnapshotPublisherService.cs) checks canonical health before signing a new local snapshot |

Remembered keys are installation-local and excluded from the synchronized canonical commitment. Recalculating a mutable integrity hash does not replace the checkpoint signature. Failed decryption with an unconfirmed password is insufficient to diagnose corruption.

## Fault isolation

| Evidence | Behavior |
| --- | --- |
| Invalid transport/envelope authentication | Reject before admitting the payload as synchronization or recovery evidence |
| Authenticated snapshot fails keyed verification | Isolate the origin revision; unrelated healthy origins remain usable, and verified higher evidence can supersede an ordinary fault |
| Retained envelope cannot be reauthenticated | Preserve the retained bytes as isolated evidence; missing historical authority or damaged bytes alone do not prove a signed fork |
| Canonical content/checkpoint failure | Block operations requiring trusted canonical state, including publication; accept eligible recovery evidence |
| Same revision with different hashes, non-dominating history, or conflicting local origin | Retain a terminal fork; a higher revision alone cannot select an authoritative branch |
| Same item version with different content, or incompatible signed key/membership transitions | Retain the conflict and fail closed |
| Account-deletion barrier | Refuse canonical recovery and recreation of that user ID |

Fault attribution uses the signed envelope's account, origin device, installation, epochs and hashes. The transport peer is retained only as a relay diagnostic. A deterministic item or control-plane conflict is not attributed exclusively to an installation merely because its envelope exposed it.

[UserSyncFaultPolicy](../../Common/Backend/Sync/UserSyncFaultPolicy.cs) derives recovery eligibility from the existing scope, category and status. Terminal records retain their original identities, hashes and diagnostic code on repeated observations. Ordinary origin supersession requires a verified higher revision in the same origin/key namespace; it cannot clear login, canonical or authority conflicts. Canonical-health success cannot clear a terminal fault or bypass publication blocking. A local inability to publish uses the existing local-state-unavailable protocol outcome, without blaming a remote peer.

Locked relays can authenticate signed envelopes without verifying decrypted contents. Isolated/recovery-only evidence is excluded from ordinary relay. An authorized installation holding a valid signing identity and user key can create valid edits; signed device removal ends that authority.

## Automatic canonical recovery

[UserDataRecoveryCoordinator](../../Common/Backend/Services/UserDataRecoveryCoordinator.cs) acquires per-user lifecycle ownership and rechecks deletion, structural health, current authority/epochs, and terminal conflicts. It reauthenticates retained current-epoch snapshots and verifies complete logical contents with a trusted key. An otherwise unconfirmed password-derived key must be confirmed by eligible healthy evidence before canonical decryption failure is attributed to corruption.

Before candidate inspection, the coordinator verifies that exactly one current authorization belongs to this installation, covers the authoritative key and membership epochs, matches its signing/agreement keys, certificate and device type, and has no retirement evidence. Missing, removed, substituted or contradictory authority returns `LocalInstallationUnauthorized`; possession of a valid encryption key does not create membership. The authority is checked again before reconstruction commits.

Reconstruction deterministically merges healthy evidence. Independently authenticated local child components can be preserved when the local signed checkpoint/root still prove them. Item versions and causal references survive reconstruction. Corrupt-only data and unproven UI edits cannot be reconstructed.

Recovery retains per-user lifecycle ownership across capture, preparation, revalidation and commit. [UserCanonicalRecoveryStore](../../Common/Backend/Repositories/UserCanonicalRecoveryStore.cs) captures the canonical row and links, checkpoint, applied control state, deletion and pending critical-control fences, faults, eligible evidence and historical membership/removal proofs in one SQLite read snapshot. The snapshot ends before signature verification, decryption, deterministic reconstruction, partial salvage and encryption. The captured rows and buffers belong exclusively to that attempt; speculative output has separate ownership and never stages repository mutations.

The store also captures a domain-separated content commitment over the relevant account rows, including retained envelope bytes/statuses, terminal evidence, journal state, local publication counters, revision knowledge and login projection. After preparation, an immediate write transaction compares that commitment against current storage and rechecks the local installation identity and authorization. Whole account-scoped evidence sets are compared so an added candidate, newly discovered fault, changed cutoff or concurrent maintenance cannot silently change the recovery decision. Comparisons include ciphertext and signed checkpoint bytes, rather than relying on timestamps or mutable integrity hashes. Changes conservatively return `TransactionConflict` without publishing, acknowledging a merge or diagnosing corruption; existing scheduled scans and subsequent evidence/manual triggers can retry. No immediate retry loop is introduced. A permanent deletion barrier always wins.

Only after revalidation does the coordinator apply the prepared replacement to a freshly read canonical row, retaining repository concurrency checks and installation-local remembered access. One transaction writes recovered encrypted data, the signed checkpoint, merged knowledge, effective login projection, fault transitions, local publication, and outgoing queue work. Candidate isolation and cryptographic-conflict diagnoses are deferred until the same input revalidation; speculative conflicts cannot become terminal based on stale evidence. Failed writes, cancellation and capacity refusal roll back together and discard staged mutations.

Prepared publication binds the exact verified encrypted output to an independently retained commitment and still verifies the current signed checkpoint and publication blockers. It avoids decrypting the same reconstructed vault again inside the write transaction. Revision allocation, current merged coverage, signing and durable queue/projection updates remain transactional. Normal publication and other callers retain their full health gates.

Decrypted verification bundles and salvage components are disposed before preparation returns. Discarded or cancelled output is cleared; read-only ciphertext can be shared within one attempt without duplicating damaged multi-megabyte payloads. Preparation records only the maximum observed item version, without touching the shared clock. That floor is observed after successful commit; normal committed reads observe it again if clock persistence or the process fails. Session/cache invalidation, conservative tombstone collection and outgoing activation also follow commit. Post-commit failures do not undo recovered durable data.

Login, Remember Me startup, newly received evidence, publication faults, and [UserDataRecoveryHostedService](../../Common/Backend/Services/Hosted/UserDataRecoveryHostedService.cs) can request recovery. Scheduled requests coalesce per user and retry with backoff. Maintenance excludes profiles with terminal faults or retained fork rows, including a terminal canonical fault. Evidence-triggered attempts still recheck those blockers before inspecting candidates, and do not revive terminal retry state. Each attempt rechecks canonical health after acquiring ownership so an older attempt cannot overwrite a completed recovery.

## Confirmed device-history repair

Device-history repair can reconstruct damaged local history only while the current installation still holds verified account authority and no terminal history conflict exists. The confirmed path verifies the master password, preserves the current local vault, detaches peers, and pauses synchronization for the affected profile.

Missing local membership, an ended authorization, conflicting signed control state, quarantined control operations, terminal faults, and retained fork rows block both automatic and confirmed reset. A confirmation issued before such evidence arrived cannot override the blocker. Reset revalidates authority and the reviewed evidence inside its transaction before staging a journal or replacement history. It does not mark terminal faults recovered/superseded or relabel fork envelopes as ordinary bad evidence. The existing repair path for damaged local counters and uncontested, locally corrupted history remains available.

[UserRecoveryJournalService](../../Common/Backend/Services/UserRecoveryJournalService.cs) stages installation-protected backup data inside the encrypted database and reads it back before reset commit. The journal and reset commit together. Successful follow-up verification removes redundant backup data. Unresolved snapshot, fork, or operation evidence remains protected as required by its authenticated state. Journals are installation-local and excluded from enrollment.

Unresolved revocation contexts are marked `HistoryResetBlocked` before live authority is replaced. Original receipts retain the signed operation hash and exact historical authorization identity. Reset is neither application acknowledgement nor signed retirement, even if the global installation origin remains unchanged.

A reset context is dormant only when it has no live operation in replacement history, no authority/receipt conflict, and fully verified frozen operation, membership chain, and retained receipts. It stays blocked and retained, but does not independently schedule networking that the receipt protocol cannot complete. A later live operation makes the context actionable again. Other live, ambiguous, or incomplete blockers remain conservative delivery requirements.

Frozen public receipt history cannot authorize current snapshots or restore removed membership. Missing independently signed predecessor proofs can be repaired in the receipt-verification cache without changing live authority. [Revocation delivery](ACCOUNT_LIFECYCLE.md#revocation-delivery) defines those proof and completion rules.

## User-authorized device recovery

Enrollment is a separate interactive operation. Fault handling, canonical reconstruction, history repair, journal maintenance, local cleanup and device removal do not open enrollment, commit a replacement addition or transfer bootstrap data. A new local identity inherits no authority.

Ordinary removal retains its session, master-password and signed-cutoff checks. [UserForkRecoveryService](../../Common/Backend/Services/UserForkRecoveryService.cs) provides one exceptional removal proof, revalidated before signing inside the same lifecycle-owned transaction:

- The explicitly selected target is remote, non-genesis and has one exact active installation authorization. The account is still at key epoch 1.
- The local installation remains authorized. Control epochs agree with canonical state, and every membership transition from genesis is present, applied, independently authenticated and uncontested.
- Exactly one retained `SameRevisionFork` belongs to the target's account/device/installation/key namespace. Both original envelopes have valid signatures, the same revision and membership epoch, and different hashes matching the original fault. No other active fault is present.
- The target authored no retained control operation. Local merged knowledge contains no target revision.
- A valid installation checkpoint and an authenticated healthy snapshot exactly match the current canonical encrypted generation, versions and blob timestamps. That snapshot neither originates from nor reports causal coverage of the target.

The existing signed `DeviceRemoval` payload carries a domain-bound `ForkExclusion`: exact authorization ID and both original envelope byte arrays. All target snapshot/control cutoffs are zero. The whole payload remains bound by the signed operation identity. This excludes all target history rather than accepting either branch; relayed snapshots claiming positive coverage of that installation are rejected too. Existing bounded control framing may refuse a proof pair that cannot fit. Missing originals, prior target merges, key rotations, local/genesis forks, unknown history, multiple faults and authority conflicts remain blocked.

A provably independent canonical copy can be opened after full credential and bundle verification so the user can reach existing device management. Normal mutations and publication stay blocked while the fork is active; login does not rewrite canonical data or start deferred merge work. Other profiles retain their own eligibility.

The signed removal, membership/cutoffs, local application receipt, fault retirement, encrypted device tombstone and next publication commit together. An independent receiver can apply the signed removal while locked. Its original snapshot becomes `RetiredForkEvidence`, and an existing matching fault becomes `RetiredOrigin`, preserving hashes and diagnostics without a recovery timestamp or superseding revision. Ordinary edits and synchronization can then resume among authorized unaffected devices. Delivery still requires the normal receipt and custody proof; removal does not imply completion.

A receiver that already merged target data, or cannot prove its canonical independence, applies revocation without replacing its vault. It retains a terminal canonical/fork blocker and all uncertain data. Recovery never chooses a branch or rolls back potentially unique edits. Historical fork snapshots are excluded from ordinary relay/recovery and remain protected from compaction, journal cleanup and history reset. Tombstone GC conservatively retains tombstones while that historical fork evidence remains.

## Explicit local profile cleanup

[UserLocalProfileService](../../Common/Backend/Services/UserLocalProfileService.cs) removes one local copy only after the exact local installation's signed disconnection has been applied. The login view requires its username, master password and a separate acknowledgement that local-only changes will be lost. Authentication verifies the encrypted account root and username component; damaged unrelated child data does not authorize a different account's erasure.

Cleanup uses a lifecycle-owned transaction and never calls `DeleteUserAccountAsync` or emits global deletion. It erases the selected mutable local profile, pending enrollment commits, ordinary snapshots, checkpoint and journals. Signed controls, membership/cutoffs, receipts, fork envelopes, fault metadata, revision knowledge and permanent deletion barriers survive. After commit, relevant sessions/remembered access are invalidated and runtime eligibility is refreshed. Unrelated profiles are retained. This is logical removal through supported encrypted storage, not a promise of physical secure overwrite of SQLite pages.

The warning requires disconnection to reach the faulty installation first; local erasure cannot stand in for remote revocation. Cleanup neither renews identity nor reconnects anything. Installation identity is shared by all profiles, and per-profile renewal is unavailable. A retired identity remains rejected even after cleanup. Fresh manual installation setup affects all its profiles, so their data must first be preserved on authorized devices. Reconnection then uses the normal explicitly approved +/code workflow from a still-authorized device.

## Evidence format and storage

Synchronization protocol 17 and enrollment payload 8 include the optional signed fork-exclusion proof and original competing retained envelope. `UserSyncSnapshots.ConflictingEnvelopePayload` is a bounded nullable BLOB. Database format 18 removes the obsolete profile-delete cascades from snapshots, revision knowledge and fault evidence, allowing explicit cleanup to preserve them. Earlier development database formats are unsupported and require explicit recreation; startup never silently deletes or migrates them. Existing admission accounting includes the second envelope in row/category usage and preserves configured budgets. Existing old faults without the second signed original cannot qualify for the exception. All existing database, WAL, row-count and category byte limits still apply; capacity failure rolls back without discarding evidence.

## Availability limits

Recovery requires sufficient authenticated evidence and usable key material. It does not override account deletion, choose between terminal authority forks, invent another installation's receipt, or recover unobserved data whose only copy is corrupt. A structurally unreadable database and a readable vault with damaged canonical account data are different failure modes; row-level reconstruction does not repair SQLite structure.
