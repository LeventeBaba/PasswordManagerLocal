# Integrity and recovery

The backend separates structural database failure, damaged local canonical data, invalid snapshot-origin evidence, and conflicting signed authority. Each condition has its own admission and recovery boundary. Fault diagnostics retain identifiers, hashes, affected scopes, and recovery state rather than plaintext vault content or keys.

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
| Canonical content/checkpoint failure | Block operations requiring trusted canonical state, including publication; accept eligible recovery evidence |
| Same revision with different hashes, non-dominating history, or conflicting local origin | Retain a terminal fork; a higher revision alone cannot select an authoritative branch |
| Same item version with different content, or incompatible signed key/membership transitions | Retain the conflict and fail closed |
| Account-deletion barrier | Refuse canonical recovery and recreation of that user ID |

Locked relays can authenticate signed envelopes without verifying decrypted contents. Isolated/recovery-only evidence is excluded from ordinary relay. An authorized installation holding a valid signing identity and user key can create valid edits; signed device removal ends that authority.

## Automatic canonical recovery

[UserDataRecoveryCoordinator](../../Common/Backend/Services/UserDataRecoveryCoordinator.cs) acquires per-user lifecycle ownership and rechecks deletion, structural health, current authority/epochs, and terminal conflicts. It reauthenticates retained current-epoch snapshots and verifies complete logical contents with a trusted key. An otherwise unconfirmed password-derived key must be confirmed by eligible healthy evidence before canonical decryption failure is attributed to corruption.

Reconstruction deterministically merges healthy evidence. Independently authenticated local child components can be preserved when the local signed checkpoint/root still prove them. Item versions and causal references survive reconstruction. Corrupt-only data and unproven UI edits cannot be reconstructed.

One transaction writes recovered encrypted data, the signed checkpoint, merged knowledge, effective login projection, fault transitions, local publication, and outgoing queue work. Deletion or an incompatible authority/generation change aborts that transaction. Failed reconstruction does not partially replace canonical state. Session/cache invalidation and outgoing activation follow commit; a post-commit activation failure does not undo recovered durable data.

Login, Remember Me startup, newly received evidence, publication faults, and [UserDataRecoveryHostedService](../../Common/Backend/Services/Hosted/UserDataRecoveryHostedService.cs) can request recovery. Scheduled requests coalesce per user and retry with backoff. Each attempt rechecks canonical health after acquiring ownership so an older attempt cannot overwrite a completed recovery.

## Confirmed device-history repair

Device-history repair handles a signed control history that cannot support continued synchronization. The confirmed path verifies the master password, preserves the current local vault, detaches peers, and pauses synchronization for the affected profile.

[UserRecoveryJournalService](../../Common/Backend/Services/UserRecoveryJournalService.cs) stages installation-protected backup data inside the encrypted database and reads it back before reset commit. The journal and reset commit together. Successful follow-up verification removes redundant backup data. Unresolved snapshot, fork, or operation evidence remains protected as required by its authenticated state. Journals are installation-local and excluded from enrollment.

Unresolved revocation contexts are marked `HistoryResetBlocked` before live authority is replaced. Original receipts retain the signed operation hash and exact historical authorization identity. Reset is neither application acknowledgement nor signed retirement, even if the global installation origin remains unchanged.

A reset context is dormant only when it has no live operation in replacement history, no authority/receipt conflict, and fully verified frozen operation, membership chain, and retained receipts. It stays blocked and retained, but does not independently schedule networking that the receipt protocol cannot complete. A later live operation makes the context actionable again. Other live, ambiguous, or incomplete blockers remain conservative delivery requirements.

Frozen public receipt history cannot authorize current snapshots or restore removed membership. Missing independently signed predecessor proofs can be repaired in the receipt-verification cache without changing live authority. [Revocation delivery](ACCOUNT_LIFECYCLE.md#revocation-delivery) defines those proof and completion rules.

## Availability limits

Recovery requires sufficient authenticated evidence and usable key material. It does not override account deletion, choose between terminal authority forks, invent another installation's receipt, or recover unobserved data whose only copy is corrupt. A structurally unreadable database and a readable vault with damaged canonical account data are different failure modes; row-level reconstruction does not repair SQLite structure.
