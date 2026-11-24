# Account and device lifecycle

Signed control operations determine account membership, installation authority, key epochs, device removal, and account deletion. Ordinary encrypted snapshots cannot grant authority or select a new key epoch.

## Identity and authority

Registration creates a user ID, initializes key/membership epochs at one, and records genesis authorization for the exact local device and installation origin. Control envelopes bind the account, operation ID/hash, author device/origin, sequence, type, payload, and base/resulting epochs.

[UserControlOperationInboxService](../../Common/Backend/Services/UserControlOperationInboxService.cs) verifies historical authorization and applies transitions under per-user lifecycle ownership. Addition/removal advances the membership epoch by one. Missing predecessors remain pending. Incompatible transitions preserve the last uncontested authority and record a conflict.

## Enrollment

Enrollment must be initiated deliberately through the existing interactive Add Device (+) workflow. The authorizing source requires a current authenticated profile session and the target must present a fresh interactive enrollment session/code. Saved codes, synchronization retries, faults, history repair and newly generated local identities are not authorization to enroll or transfer bootstrap data. Applying an already signed addition during synchronization preserves its original user-granted authority; it is not permission to create another addition.

The source authenticates the target device/origin, signing and agreement keys, TLS fingerprint, and device type. It merges eligible pending state, commits the signed addition and enrollment record, then transfers encrypted bootstrap data. A failed transfer can leave a valid committed membership with outstanding transfer work.

The same target identity can retry that transfer. Removing the committed membership requires a signed removal. The bootstrap contains canonical encrypted state, authority history/cutoffs, revision knowledge, retained snapshot evidence, and the signed target addition. Source private keys, remembered local keys, sessions, and decrypted caches are excluded.

The importer verifies the authority graph before persistence and initializes sequencing for the target's own installation identity. [DeviceEnrollmentRegistrationService](../../Common/Backend/Services/DeviceEnrollmentRegistrationService.cs) and the enrollment services implement this boundary.

## Device removal

Removal verifies the master password and local author, incorporates eligible pending data, and commits signed cutoffs for the removed installation's covered key epochs. Cutoffs bound accepted snapshot revisions and control sequences. Valid earlier history remains relayable within those bounds. Re-enrollment of a removed origin requires a fresh installation origin.

An unresolved terminal fork or control-plane conflict normally blocks initiating removal, including when only a durable fault record remains. The narrow [fork-exclusion proof](RECOVERY.md#user-authorized-device-recovery) permits an authenticated user to remove one exact remote non-genesis installation only when both original signed branches and a complete uncontested authority chain prove that canonical state is independent of all its history. Key epoch must still be 1, no target revision may have been merged, and no unrelated fault or target-authored control operation is allowed. Session, password and explicit target selection remain necessary.

The existing removal operation carries both signed originals and their authorization ID; its zero cutoffs exclude every target revision/control sequence. Payload hash and operation signature bind that proof. Ordinary removals keep their existing bounded-history semantics. An unaffected receiver retires the fault as historical evidence; an unproven or contaminated receiver keeps its data and terminal blocker. No fork branch is selected, replacement membership minted or installation identity rotated. Applied receipts and completion remain governed by the normal revocation protocol.

A locked installation can apply removal. On the removed installation, application clears saved account access and the local sync link. Session/runtime invalidation follows commit. Unobserved changes held only by an offline removed installation can be lost.

## Master-password changes

A signed key-epoch replacement re-encrypts canonical data while preserving logical item versions. It updates effective login metadata and invalidates sessions and remembered keys that use the previous epoch. Historical snapshots remain subject to key and membership authorization; they do not override the current epoch.

## Username projection and login

[UserLoginIdentityProjectionService](../../Common/Backend/Services/UserLoginIdentityProjectionService.cs) stores salted username lookup metadata, the general-data version, and source evidence. The actual username remains inside encrypted general data. An authenticated pending snapshot can update effective lookup while the installation is locked; the inbox commits that projection with snapshot storage before acknowledging it.

Projection selection uses current epochs, deterministic general-data versions, and eligible authenticated evidence. Bundle verification checks signed lookup metadata against the decrypted username/version. Conflicting or isolated evidence can block login. Multiple matching projections are ambiguous; lookup does not fall back to the older canonical username.

[UserLoginService](../../Common/Backend/Services/UserLoginService.cs) resolves effective identity, rechecks it under lifecycle ownership, verifies key/canonical/security state, then resolves identity again before issuing a session. Local rename commits encrypted general data, effective lookup, and publication work together. Deferred post-login reconciliation waits for the initial read gate or its timeout; releasing the gate does not join that work.

## Local profile access

Canonical recovery requires the current installation's exact active authorization. History repair cannot reconstruct missing or ended local membership merely from a password, an old local session or confirmation of a reset. The local device and origin identity belongs to the installation as a whole, so renewing it for one profile would affect other profiles.

The login view exposes explicit local-only erasure after an authenticated signed removal of this exact installation is already applied. Username/master-password verification and a separate warning acknowledgement are required. One transaction removes only the selected mutable local profile while preserving signed controls, membership/cutoffs, receipts and original fork/fault evidence. Session/key invalidation and runtime refresh follow commit. It does not call account deletion, revoke another device or replace remote signed removal. Local-only changes are lost only after that explicit acknowledgement.

Cleanup reports a durable removal followed by failed session cleanup/runtime reconciliation as a partial commit, rather than a clean failure. In-memory remembered key material is cleared even if session invalidation fails; background eligibility is still reconciled after releasing the profile lifecycle lock. Incomplete follow-up requires restart and review, not a blind second deletion. The frontend closes the destructive form for a confirmed partial commit or an unknown transport outcome and never uses either result to start enrollment.

Cleanup does not create a fresh identity. The retired installation remains ineligible for enrollment; fresh manual setup is installation-wide and can affect every local profile. [Recovery](RECOVERY.md#explicit-local-profile-cleanup) describes the constraints and separate user-initiated enrollment step.

## Account deletion

[UserDeletionService](../../Common/Backend/Services/UserDeletionService.cs) commits the signed deletion operation, permanent user-ID barrier, and mutable-state cleanup together for local deletion. A receiver can retain the authenticated barrier before cleanup completes; the barrier blocks access while application is retried.

[UserAccountDeletionCleanupService](../../Common/Backend/Services/UserAccountDeletionCleanupService.cs) removes mutable account data, local access, snapshots, ordinary queue work, and recovery journals. The deletion operation, barrier, and historical verification evidence survive. Offline copies remain until the instruction reaches them.

The barrier prevents snapshots, recovery, or enrollment from recreating that user ID. Reusing the username in a new registration creates a different user ID.

## Revocation delivery

[RevocationDeliveryService](../../Common/Backend/Services/RevocationDeliveryService.cs) derives delivery progress for device removal and account deletion from signed evidence. Completion is not an independent boolean. Completed operations retain their removal cutoffs, historical authority, and deletion barriers.

### Required installations

The author signs a `RevocationMembers` declaration containing historical authorization IDs, device/origin identities, public keys, membership/key intervals, and exact addition/removal references. Account deletion's signed `KnownMembers` summary must agree with that declaration.

Recipient derivation verifies the complete signed membership predecessor chain from genesis through the revocation's base epoch. Missing transitions, contradictory identities, omitted known members, or forks block completion. Bounded auxiliary `RevocationHistoryProofs` can supply independently signed predecessor envelopes without changing the original operation identity. Proofs are anchored by the exact references in the signed declaration and cannot nest auxiliary proof lists.

| Operation | Required application receipts |
| --- | --- |
| Account deletion | Every installation active at the signed base, including the author |
| Device removal | Every remaining active installation, including the author; the removed target is best-effort only when exact signed cutoff evidence supports its exemption |

An obligation identifies device, installation origin, and authorization together. A device ID alone is insufficient. Earlier retirement is accepted only from verified signed history. A required offline installation remains pending without a timeout.

Frozen public history is a receipt-verification context. It does not restore live membership, authorize snapshots, or recreate an account. Authoritative deletion can apply before all receipt-completion proofs are available.

### Receipt verification

[RevocationReceiptService](../../Common/Backend/Services/RevocationReceiptService.cs) verifies format-2 receipts. Ed25519 signs domain-separated canonical fields; SHA-256 over those fields and the signature identifies the exact receipt.

| Signed fields | Binding |
| --- | --- |
| `Version`, `UserId`, `OperationId`, `OperationHash`, `OperationType` | Receipt namespace and exact revocation |
| `KeyEpoch`, `MembershipEpoch` | Revocation's previous epochs |
| `RecipientDeviceId`, `RecipientOriginInstanceId`, `RecipientAuthorizationId`, `SignPublicKey` | Exact historical acknowledging installation and key |
| `Kind`, `AttestedReceiptHash` | Application/storage claim, or custody of one exact application receipt |

| Kind | Meaning |
| --- | --- |
| `DurablyStored` | Storage claim; does not count as application |
| `Applied` | The installation's signed application result, committed with successful application |
| `AppliedReceiptStored` | A distinct required installation durably holds the referenced `Applied` receipt |

Verification checks the signed operation, epochs, exact historical identity/key, intervals, and signature. Custody requires the referenced valid application receipt and a distinct required signer. Self-custody, exempt-target custody, recursive custody, and signatures attributed to another installation are rejected.

### Persistence and exchange

| Table | Contents |
| --- | --- |
| `RevocationReceiptEvidence` | Frozen signed operation, public membership/proofs, reset/conflict blockers |
| `RevocationDeliveryReceipts` | Original signed receipt bytes/hash and exact historical recipient/kind/attested-hash identity |
| `RevocationEvidenceDebts` | Exact receipt hashes known missing from a required peer's committed inventory |

These tables survive account/history cleanup and contain public evidence rather than private keys or decrypted vault contents. Receipt uniqueness includes operation, device/origin/authorization, kind, and attested hash.

Application, frozen context, and the local `Applied` receipt commit in the same transaction. Deletion stages its application receipt after successful cleanup. Failure rolls back application evidence with the transaction. Receipt import commits received application evidence and locally generated custody together; incompatible authenticated content persists a conflict blocker. Exact duplicates are idempotent.

Control anti-entropy exchanges bounded summaries, exact requests, and original signed responses. Operations precede their receipts; application receipts precede dependent custody. Responses must match outstanding identity/hash requests. A fresh committed inventory acknowledges evidence debt; transport delivery alone does not clear it. Limits are defined in [SyncConstants](../../Common/Backend/Constants/SyncConstants.cs).

### Completion and blocked work

Every required installation needs a valid application receipt or a separately proven retirement by a later completed removal/deletion for that exact historical identity. Reset, a replacement installation, or recovered canonical data cannot discharge an obligation.

When multiple required installations remain, each application receipt also needs signed custody by another remaining required installation. Known outgoing evidence debt must be cleared. Successor evaluation is independent of receipt arrival order and rejects dependency cycles.

| `RevocationDeliveryState` | Meaning |
| --- | --- |
| `Pending` | Required evidence is absent |
| `PartiallyDelivered` | Valid progress exists but obligations remain |
| `Completed` | Application/retirement, custody, and known debt requirements are satisfied |
| `Blocked` | Authority, receipt, deletion-barrier, or reset evidence prevents a safe decision |

`WorkKind` separately identifies actionable recipient/custody work, conflicts, unverified evidence, and dormant reset contexts. Unresolved does not always imply actionable networking. Narrowly verified reset contexts without a live operation can be dormant while remaining blocked and retained; see [history repair](RECOVERY.md#confirmed-device-history-repair).

[Runtime reconciliation](SYNCHRONIZATION.md#activation-and-service-ownership) retains in-flight ownership through the final response flush, then reevaluates committed delivery and ordinary eligibility. Delivery completion stops active work only when no independent reason remains.
