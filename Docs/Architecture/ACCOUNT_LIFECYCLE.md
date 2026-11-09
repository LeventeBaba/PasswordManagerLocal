# Account and device lifecycle

Signed control operations define which installations can participate in an account and which key epoch is current. Registration initializes key and membership epochs at one and records a genesis authorization for the exact local device and installation origin. A signed key replacement, device addition, device removal, or account deletion carries its author, sequence, and applicable epochs. [The control inbox](../../Common/Backend/Services/UserControlOperationInboxService.cs) checks historical authorization and applies transitions under the per-user lifecycle coordinator.

An addition or removal advances the membership epoch by one. Operations whose predecessors are missing remain pending. Incompatible transitions from the same base are quarantined. The last uncontested authority remains in place. Ordinary encrypted snapshots cannot change membership or authorize a new key.

## Enrollment

The source authenticates the target's device and installation identities, signing and agreement keys, TLS fingerprint, and device type. It merges eligible pending state, commits the signed addition and enrollment record, then sends the encrypted bootstrap. The transfer runs after the membership commit, so a transfer failure can leave a valid enrolled membership with transfer work still outstanding.

The same target identity can retry the committed transfer. Removing the membership requires a signed removal. The bootstrap carries canonical encrypted state, authority history and cutoffs, revision knowledge, retained snapshot evidence, and the signed target addition. Private keys, local Remember Me keys, session tokens, and decrypted caches stay on the source. The importer verifies the graph before persisting it and initializes local sequencing for the target's own identity.

## Removal and key rotation

Removal authenticates the user and the local author, merges eligible pending data, and records signed cutoffs for the removed installation across its covered key epochs. These cutoffs bound the highest accepted snapshot revision and control sequence. Valid pre-removal history can still be relayed within those limits. A removed origin is retired, so re-enrollment uses a fresh installation origin.

A locked device can apply signed removal. If it is the removed installation, the durable change clears its saved key and local sync linkage. Sessions and runtime state are invalidated after commit. Changes on an offline device that other peers never observed can be lost when it is removed.

A master-password change replaces the key epoch and re-encrypts canonical data while preserving logical item versions. It updates effective login metadata and invalidates old sessions and remembered keys. A pre-rotation snapshot can retain historical value, while current key authorization governs which data can be applied.

## Effective username and login

The [login identity projection](../../Common/Backend/Services/UserLoginIdentityProjectionService.cs) stores salted username lookup metadata and the corresponding general-data version and source evidence. Canonical general data stores the username inside its encrypted payload. A signed snapshot can change effective login lookup while the receiver is locked. The inbox updates the projection with the pending snapshot in the same transaction, before acknowledging storage.

Projection selection uses the deterministic general-data version, current epochs, and eligible authenticated evidence. During decryption, bundle verification compares the signed username metadata with the actual username and version. Isolated or conflicting evidence can make login unavailable until it resolves. If a username matches multiple effective projections, lookup is ambiguous. There is no fallback to the previous canonical username.

[Login](../../Common/Backend/Services/UserLoginService.cs) resolves the user, rechecks the effective identity under the lifecycle coordinator, and performs the required key, canonical data, and pending security checks. It resolves the identity again before issuing the session. A local rename commits the encrypted general-data update and effective lookup together and queues the signed publication.

## Account deletion

[Account deletion](../../Common/Backend/Services/UserDeletionService.cs) signs a dedicated lifecycle operation and records a permanent barrier keyed by user ID. A local deletion commits the operation, barrier, and mutable-state cleanup together. A receiver can retain an authenticated barrier before cleanup succeeds, and the pending barrier blocks use of the account while application is retried.

[Cleanup](../../Common/Backend/Services/UserAccountDeletionCleanupService.cs) removes mutable account data and local access, including snapshots, queue work, and recovery journals. The deletion operation, barrier, and the authority evidence needed for historical relay survive. Account deletion can therefore propagate even after the local account row is gone. Offline copies remain until the deletion reaches them.

The barrier prevents stale snapshots, recovery, or enrollment from recreating that user ID. Registration can use the same username with a new user ID.
