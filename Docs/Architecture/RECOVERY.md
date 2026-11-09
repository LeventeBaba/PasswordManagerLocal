# Integrity and recovery

The backend distinguishes structural database failure, local canonical damage, bad data from one snapshot origin, and conflicting signed history. These cases have different repair options. Fault records retain identifiers, hashes, affected scopes, and recovery state. Plaintext vault contents and keys stay out of fault diagnostics.

## Canonical integrity

The [database initializer](../../Common/Backend/Persistence/AppDatabaseInitializer.cs) runs SQLite `quick_check` before normal writes. Structural failure stops startup. A readable database can still contain damaged account data, so the backend separately verifies encrypted bundles and their relationships.

The [signed canonical checkpoint](../../Common/Backend/Sync/UserCanonicalCheckpointUtil.cs) binds the current canonical content to the local device and installation identity. It commits to the epochs, username and password metadata, integrity hash, and encrypted payloads. The remembered key is local-only and outside the signed synchronized state. Recalculating a stored integrity hash cannot replace the local signature.

The [publication gate](../../Common/Backend/Services/UserSnapshotPublisherService.cs) checks the canonical evidence before signing new snapshots. [Bundle verification](../../Common/Backend/Services/UserDataBundleVerificationService.cs) also checks decryption, nested integrity, links between components, item versions, and correspondence between username metadata and the decrypted profile. Key confidence determines whether a failed decrypt is attributable to corruption.

## Fault isolation

| Evidence | Behavior |
| --- | --- |
| Invalid transport or envelope authentication | Reject before using the payload as sync or recovery evidence. |
| Authenticated snapshot fails keyed verification | Isolate the affected origin revision. Unrelated healthy origins can continue. A verified higher revision can supersede an ordinary fault. |
| Canonical content or checkpoint fails verification | Block operations that depend on trusted canonical state, including publication. Continue accepting eligible evidence for recovery. |
| Same revision with different hashes, non-dominating history, or a conflicting local origin | Record a terminal fork. A later revision cannot resolve which branch was authoritative. |
| Same item version with different content, or incompatible signed key or membership transitions | Preserve the conflict and fail closed. |
| Authoritative account deletion | Block canonical recovery and account recreation. |

A locked relay can authenticate an envelope without evaluating its decrypted content. Isolated and recovery-only evidence is excluded from normal relay. An authorized device holding the signing identity and user key can still produce valid edits. Device removal is the mechanism for ending its authority.

## Automatic canonical recovery

[The recovery coordinator](../../Common/Backend/Services/UserDataRecoveryCoordinator.cs) first checks the deletion barrier, structural database health, authority, current epochs, and terminal conflicts. It reauthenticates retained current-epoch snapshots and fully verifies their contents with a trusted key. A password-derived key that fails against canonical data must first be confirmed by a fully verified eligible snapshot. A wrong password alone is insufficient to diagnose corruption.

Recovery deterministically merges healthy evidence and rebuilds the canonical bundle. If the signed checkpoint and local root still authenticate specific local child components, recovery can preserve those components alongside the healthy snapshot data. It preserves item versions and causal references. It cannot reconstruct edits whose only surviving copy is corrupt or unproven mutable UI state.

One transaction writes the recovered encrypted bundle, signed checkpoint, merged knowledge, effective login projection, fault status, local publication, and queue work. Deletion or an authority change can abort the transaction. After commit, the backend invalidates old session caches and keys and activates outgoing work. Login or Remember Me can then establish a fresh session from the recovered data.

Login, Remember Me, new evidence, a publication fault, and the [hosted worker](../../Common/Backend/Services/Hosted/UserDataRecoveryHostedService.cs) can initiate recovery. Scheduled attempts coalesce per user and back off when recovery cannot proceed.

## Confirmed device-history repair

Canonical recovery reconstructs vault data from trusted evidence. Device-history repair addresses a different problem, where the signed history needed to continue synchronization cannot be reconstructed. In the confirmed path, diagnostics checks the master password before resetting the affected profile's device history. The operation preserves the current local vault, detaches other devices, and pauses synchronization for that profile.

[The recovery journal](../../Common/Backend/Services/UserRecoveryJournalService.cs) is device-protected and stored inside the encrypted database. The backend stages and reads it back before committing the reset. The journal and reset commit together. After successful follow-up verification, redundant backup data is removed. Unresolved snapshots, forks, or operation evidence can remain protected until the corresponding authenticated evidence resolves them. Journals are installation-local and excluded from enrollment.
