# Application architecture

PasswordManagerLocal maintains an encrypted local vault on each installation and synchronizes approved profiles directly between enrolled devices. Shared backend code owns account operations, persistence, cryptography, and synchronization. Platform code owns processes, operating-system key protection, and frontend connectivity.

## Project boundaries

| Project area | Responsibility |
| --- | --- |
| [Common/Contracts](../../Common/Contracts) | Endpoint interfaces, DTOs, notifications, and shared boundary types |
| [Common/Backend](../../Common/Backend) | Domain services, direct SQLite persistence, security, peer protocols, and runtime hosting |
| [Common/Frontend](../../Common/Frontend) | Avalonia views/view models, UI sessions, localization, themes, and refresh coordination |
| [Common/Preferences](../../Common/Preferences) | Application preference storage |
| [Windows](../../Windows) | Frontend, Agent, IPC, Endpoint RPC, OS integration, and packaging |
| [Android](../../Android) | Activity, private bound/foreground service, runtime owner, and OS integration |

The [project map](../SOLUTION_PROJECT_ARCHITECTURE.html) shows default project references and runtime boundaries.

## Shared runtime

[Hosting](../../Common/Backend/Hosting) and its [abstractions](../../Common/Backend/Abstractions/Hosting) coordinate backend startup, ownership leases, interactive sessions, and shutdown. Backend lifetime and synchronization lifetime are separate: a ready backend or active lease does not establish healthy synchronization.

`BackendRuntimeLifetimeCoordinator` tracks interactive and background reasons. The execution profile is interactive while the frontend owns a lease and background-only while only background synchronization owns one. [Synchronization eligibility](SYNCHRONIZATION.md#runtime-eligibility) independently determines active networking, delivery work, enrollment, and passive historical ingress.

[Background settings](../../Common/Backend/Configuration/BackgroundSync) are separate from the vault and UI preferences. The platform owner reads the desired setting before acquiring background ownership. A disabled background setting does not prevent foreground synchronization under an interactive lease.

## Windows runtime

The frontend owns UI. The Agent owns the backend, vault, peer networking, endpoint host, and tray integration. Requests cross named pipes.

| Publish mode | Process layout |
| --- | --- |
| Managed / ReadyToRun | Separate frontend and Agent executables |
| Native AOT | One executable; UI entry point and `--agent` entry point run as separate processes |

Both layouts retain separate instance locks and the same IPC boundary. Windows firewall integration targets the executable running the Agent role. [Publishing](../WINDOWS_INSTALLER.md) defines the product layout, sign-in registration, installation, and removal behavior.

### IPC and commit outcomes

[Windows/IPC](../../Windows/IPC) owns named-pipe transport, framing, handshake/authorization, instance coordination, and UI registration. [Windows/EndpointRpc](../../Windows/EndpointRpc) owns endpoint payloads, client/proxy behavior, and server dispatch. [EndpointOperationManifest](../../Windows/EndpointRpc/Contracts/Metadata/EndpointOperationManifest.cs) assigns stable operation IDs and commit models.

| Commit model | Boundary |
| --- | --- |
| `NoDurableMutation` | Read-only operation |
| `SingleAtomicCommit` | One authoritative commit or indivisible local mutation |
| `CommitThenFollowUp` | Durable commit followed by session, cache, or runtime work |
| `MultiStageRecoverableCommit` | Durable subset can precede a later failure |
| `ExternallyObservableSideEffect` | Process, network, listener, or session effect independent of a database commit |

[Dispatcher](../../Windows/EndpointRpc/Server/EndpointRpcDispatcher.cs) results distinguish `NotCommitted`, `PartiallyCommittedRecoveryRequired`, and `OutcomeUnknown` where applicable. Process restart requirements are separate from commit outcome. Authoritative readback or an operation-specific recovery path resolves uncertain effects; blind mutation replay is unsafe.

## Android runtime

[MainActivity](../../Android/Frontend/MainActivity.cs) binds to the private [PasswordManagerBackgroundService](../../Android/Frontend/Runtime/PasswordManagerBackgroundService.cs) in the app process. [AndroidRuntimeServiceHost](../../Android/Runtime/AndroidRuntimeServiceHost.cs) owns the backend composition and foreground/background leases.

Each activity attachment has a generation. Replacement/detachment revokes the old generation's endpoint and notification authority. Enrollment, database reset, and attachment-owned settings changes require the current authorized attachment. Activity closure cancels or drains work according to its commit boundary.

The [manifest](../../Android/Frontend/Properties/AndroidManifest.xml) declares a non-exported `connectedDevice` foreground service and boot/package-replacement receivers. Restoration respects the saved preference, protected-storage availability, first unlock, and Android service-start restrictions.

## Background ownership and health

| Platform | Required synchronization / retry | Intentional idle |
| --- | --- | --- |
| Windows | The enabled background preference retains the existing Agent/backend owner after frontend closure | The lightweight background lease remains for IPC and justified passive ingress; ordinary discovery is inactive |
| Android | Foreground/background ownership remains while eligible synchronization, actionable revocation delivery, or recoverable retry requires it | Foreground/background ownership is released; the enabled preference remains stored |

An attached Android activity can keep an idle backend alive and reactivate foreground ownership after a committed local change. With no owner, the service/backend can stop; historical traffic is unavailable until a supported local/restoration action starts it again. The app has no periodic WorkManager synchronization mode.

Platform status distinguishes enabled idle, startup, running synchronization, recoverable degradation, unavailability, and restart-required failure. Foreground notification presence and backend readiness are separate from synchronization readiness. Unsafe Android cleanup closes attachment authority and uses the host's process-termination path. Neither platform creates a second backend owner to bypass an unsafe runtime latch.

## Persistence and transactions

[SqliteSession](../../Common/Backend/Persistence/SqliteSession.cs) provides direct SQLite repository/unit-of-work transactions over SQLite3MC. [AppDatabaseInitializer](../../Common/Backend/Persistence/AppDatabaseInitializer.cs) creates the [schema](../../Common/Backend/Persistence/Schema/InitialSchema.sql), installs admission guards, and performs structural integrity checks. [DbConfigManager](../../Common/Backend/Security/DbConfigManager.cs) checks the protected configuration's database version. [DatabaseEncryptionMigrator](../../Common/Backend/Persistence/DatabaseEncryptionMigrator.cs) handles supported legacy SQLCipher encryption separately from schema compatibility.

Windows DPAPI and Android Keystore protect the installation's database key. The vault file alone cannot transfer that OS protection to another installation. Enrollment transfers authorized profile data instead of copying protected local keys.

[Repositories](../../Common/Backend/Repositories) implement persisted reads/writes. Services own validation, lifecycle decisions, and multi-repository transactions. Per-user lifecycle ownership serializes canonical and authority-sensitive work. Durable mutation, checkpoint/publication state, and queue work commit together; transport activation follows commit. Post-commit failures preserve the operation's actual durable outcome.

The storage admission policy below bounds retained logical data across all vault tables. Bounded protocol messages, latest-per-origin replacement, and causal collection remain separate correctness controls. The native file limits below additionally bound SQLite-controlled file growth, including transaction-commit overshoot and temporary working files.

### Storage admission and limits

[SqliteStoragePolicy](../../Common/Backend/Persistence/SqliteStoragePolicy.cs) installs database triggers covering every INSERT, UPDATE and DELETE on the 26 application tables. The 78 AFTER triggers enforce logical accounting; 84 BEFORE triggers check WAL pressure on those tables and the two accounting tables. Startup rejects an unclassified persistent table or an incomplete admission installation. The schema contains two additional fixed inventories: `StorageBudgets` has nine category rows and `StorageUsage` has one row per controlled table. SQLite's internal `sqlite_sequence` has at most one entry per AUTOINCREMENT table; this schema has two such tables.

Every row is charged 128 bytes plus the byte length of every column cast to a BLOB. This counts UTF-8 text, ciphertext, signatures, hashes, keys, and unexpectedly typed SQLite values. It is a logical admission measure, not a measurement of index, page, or journal bytes. The positive per-row charge also bounds cardinality: a category can never retain more than its budget divided by 128 rows. Actual rows consume more. Each individual row has a separate size limit.

Budgets from [StorageCapacityConstants](../../Common/Backend/Constants/StorageCapacityConstants.cs) apply across the installation, including all profiles and retained deleted-account evidence:

| Category | Charged-byte budget | Purpose |
| --- | ---: | --- |
| Content | 1 GiB | Canonical encrypted users, links, login projections |
| Snapshots | 1 GiB | Exact retained envelopes, knowledge, publication state, checkpoints |
| Control | 256 MiB | Signed lifecycle operations, membership, cutoffs, enrollment history |
| Revocation | 512 MiB | Frozen authorizations/proofs, receipts, custody, peer debts |
| Barriers | 64 MiB | Permanent deleted-user barriers |
| Recovery | 512 MiB | Fault identities and protected recovery journals |
| Identity | 32 MiB | Peer device identities and installation identity |
| Work | 128 MiB | Queues, model bookkeeping, generic tombstones, version clock |
| Ingress | 256 MiB | Exact received revocation envelopes awaiting admission/application |

The total logical budget is 3,808 MiB. Independent category budgets prevent ordinary content and snapshot admissions from consuming the control, revocation, barrier or recovery allowances. These reserves remain finite and do not reserve physical free disk space. Index overhead, fragmentation, transaction space, or external disk exhaustion can prevent a write before its logical budget is reached.

There may be at most 256 local profiles, matching the protocol inventory's user limit. Existing content validation allows 1,000 live passwords, 1,000 tags and 1,000 custom colors per profile, with finite retained encrypted tombstone lists. These are embedded in `Users.EncryptedUserPasswordsDataPayload`; they are not separate database tables. Existing field validation still applies. Device metadata and all embedded lists additionally encounter the encrypted row and aggregate content bounds.

Metadata rows are limited to 64 KiB including their charge. A snapshot/control/critical-ingress row may hold its existing 4 MiB protocol envelope plus 64 KiB of metadata. Critical ingress additionally has a 1,024-row installation-wide cap. Canonical user rows, frozen revocation contexts and recovery-journal rows are limited to 129 MiB, including all columns and the charge. A journal's existing plaintext limit remains 128 MiB. User-data serialization and decompressed JSON are limited to 128 MiB per blob, with a 129 MiB encrypted-output bound. Decoding checks the complete decompressed stream, including trailing input; a limit failure returns invalid input through the codec's existing contract. AES frame headers are capped at 1 MiB before buffer rental; ordinary writers still use 64 KiB frames.

The generous limits accommodate substantial legitimate data and offline evidence. They do not promise every combination of maximum-size profiles, histories and envelopes can coexist. An individual database or protocol bound may be reached first.

### Persistent growth inventory

All variable-length columns participate in row charging, including nullable diagnostic text. No table relies on currently observed small size as its bound. Collections requiring permanent or unresolved evidence remain subject to their shared category budget.

| Collection | Creation and update paths | Removal / retention rule | Admission category |
| --- | --- | --- | --- |
| `Users` | Registration, enrollment import, keyed mutations, merges, recovery, rotation | Authoritative account deletion; encrypted item tombstones require causal collection | Content; 256 rows |
| `UserDevices` | Enrollment, merged link/settings changes, removal projection | Account/device cascades and existing lifecycle cleanup | Content |
| `LocalUserDevices` | Enrollment, registration, local sync preference | Account/local identity cascade; one local link per user | Content |
| `UserLoginIdentityStates` | Verified canonical/received username projection | Account cascade; one projection per user | Content |
| `Devices` | Peer enrollment/import, presence/security updates | Existing unlinked/self-device cleanup; discovery alone does not create arbitrary peers | Identity |
| `LocalDeviceIdentity` | Protected local identity generation and sync setting | Explicit installation reset; schema enforces singleton | Identity; one row |
| `SyncItems` | Local mutation/model queueing | Existing unreferenced bookkeeping cleanup and deletion cleanup; unique model pair | Work |
| `SyncQueueItems` | Per-target enqueue/upsert, acknowledgement | Existing queue cleanup/cascades; unique device/item pair | Work |
| `SyncTombstones` | Generic model deletion/upsert | Existing model/lifecycle cleanup; never evict evidence on capacity failure | Work; existing 4,096-row cap |
| `SyncVersionClockState` | Independent atomic version-clock writes | Singleton overwritten in place | Work; one row |
| `UserSyncStates` | Publication revision reservation and content commitment | Account/history-reset cleanup; one row per user | Snapshots |
| `UserSyncSnapshots` | Local publication, authenticated ingress, merge, quarantine | Existing same-origin replacement and explicit account/history-reset lifecycle; unique origin/instance/key epoch | Snapshots |
| `UserRevisionKnowledge` | Durable stored/merged revision advancement | Explicit account/history reset only; preserve namespaces and replay/fork knowledge | Snapshots |
| `UserCanonicalCheckpoints` | Verified canonical commit/recovery | New signed checkpoint replaces the per-user row; account/history reset cleanup | Snapshots |
| `CriticalControlIngress` | Authenticated-route received revocations, before control-author verification; exact serialized-envelope hash deduplicates retransmission | Validator rejection removes only that exact input. Applied/quarantined control proof or a permanent deleted-user barrier permits matching completed-custody cleanup; unresolved envelopes remain | Ingress; 1,024 rows |
| `UserControlOperations` | Signed key/membership/removal/deletion ingress and local issuance | Historical authority remains retained across account cleanup; explicit reset follows existing journal rules | Control |
| `UserControlStates` | Local control sequence, applied epochs, conflicts | Existing lifecycle/reset rules; one row per account namespace | Control |
| `UserMembershipAuthorizations` | Genesis, enrollment/addition and signed removal | Retain ended installations for historical signature authority and revocation verification | Control |
| `UserOriginRemovalCutoffs` | Signed removal freezes accepted origin revisions/sequences | Retain stale-installation rejection evidence; explicit history reset retains required evidence | Control |
| `DeviceEnrollmentCommits` | Staged enrollment, retries, completion state | Existing enrollment/account cleanup; unique target installation | Control |
| `DeletedUserBarriers` | Verified authoritative deletion and conflict updates | Permanent; successful delivery never authorizes removal | Barriers |
| `RevocationReceiptEvidence` | Applied revocation freezes signed envelope/authorization/history context, later verified augmentation | Retained across user cleanup and history reset | Revocation |
| `RevocationDeliveryReceipts` | Verified application, storage and final-custody receipts | Existing immutable identity prevents duplicate allocation; retain required delivery proof | Revocation |
| `RevocationEvidenceDebts` | Authenticated peer evidence obligations | Existing satisfied-debt reconciliation; never infer completion solely from age | Revocation |
| `UserSyncFaults` | Health verification, quarantine, recovery scheduling | Existing fault identities are reused; resolved/terminal records remain bounded without a new purge | Recovery |
| `UserRecoveryJournals` | Transactional protected history-reset snapshot, evidence-only rewrite | Existing verified completion removes/reduces the journal; unresolved evidence stays | Recovery |

Repositories own staged row writes. Immediate SQL in `UserRepository`, queue acknowledgement, `SyncItemRepository`, `UserAccountDeletionStore`, and the independent `SyncVersionClockStore` uses the same guarded tables. Imported snapshots, control envelopes and enrollment data cannot bypass admission. Deletes and foreign-key cascades release the matching charges inside the same transaction. Singleton replacement, exact duplicate handling and queue upserts retain their existing identities.

Before admission, repeated key/membership churn, historical origin namespaces, frozen receipt contexts/debts, unresolved faults/journals, and profile creation/deletion could accumulate across an installation's lifetime. Limits in `TombstoneConstants` that bound evidence *processing* were not aggregate database budgets. Those paths now share finite byte budgets even where safe cleanup cannot progress. Repeated updates of fixed rows are logically bounded; they still require the WAL considerations below.

### Exhaustion and evidence retention

Accounting is updated by AFTER triggers in the same SQLite writer transaction as the data. An over-budget insert/update raises `ABORT`, rolling back the statement and its trigger effects. A unit-of-work flush rolls back all its staged writes or its savepoint; outer rollback also undoes accounting. Concurrent writers cannot both spend the same remaining allowance. A no-growth UPSERT needs no spare row slot. The layer does not evict records or advance any synchronization knowledge to make room.

`StorageCapacityExceededException` distinguishes logical capacity and SQLite FULL/TOOBIG failures from corruption. Ordinary staged mutations, the immediate saved-key update and version-clock writes translate these failures. Existing receipt/knowledge updates remain inside their original transactions: failed snapshot storage cannot produce a successful durable-storage receipt. Failed deletion application cannot produce an applied receipt; a deletion barrier already committed with the pending control operation remains authoritative. Deleting existing rows releases logical capacity. The application does not automatically recreate, reset or truncate a vault on capacity failure.

The frontend shows a simple item/storage-capacity message. Windows retains its existing commit-outcome rules: a capacity error is conclusively uncommitted only for an operation declared atomic; a follow-up failure does not erase a previous durable commit. Internal budget values and sensitive evidence are not included in user messages.

[CriticalControlIngressStore](../../Common/Backend/Persistence/CriticalControlIngressStore.cs) durably retains a received device-removal/account-deletion envelope in the encrypted vault **before control-author verification**. Network ingress first authenticates the peer and its historical route. Direct inbox admission uses the same retained-custody boundary. If this separate ingress budget or the physical write fails, author verification does not run and the sender receives no custody/application acknowledgement. A verified revocation can therefore survive failure of its initial normal control-operation/barrier write. The ingress row itself never means application or delivery succeeded.

The presence of retained ingress fences the affected profile. Existing session/key and cached-data access, login/remembered restoration, local mutations, publication, snapshot relay, ordinary routes and ordinary snapshot inventories observe the fence. Login uses the normal credential-failure/KDF path rather than exposing whether a supplied username identifies a fenced profile. Unaffected profiles continue to operate. A finite process-local cache accelerates the durable check; startup reconstructs it and validates the retained envelope hashes/identities before ordinary access. Unknown local write/read outcomes conservatively block access until a successful reconciliation or backend restart reloads committed state.

The existing per-user lifecycle coordinator has an explicit control-processing path. It permits revocation replay and historical prerequisites while ordinary calls stay fenced; captured work cannot retain that permission after its owner completes. While a revocation is unresolved, non-revocation control transitions may advance only up to its predecessor epochs and must respect the retained removed-origin sequence ceilings. The same restriction filters control inventories and relays; a suspended ordinary route still has a distinct authenticated control path for those prerequisites. This is an additional admission restriction, not a grant of signature authority. The existing membership/signature verifier still decides authorization, including a fresh check before applying queued non-deletion operations after another removal may have committed. Account-deletion barriers retain their existing precedence.

The existing pending-control startup recovery service retries retained ingress before ordinary pending operations. A failed attempt keeps the received envelope and fence; there is no new daemon or tight retry timer. A duplicate delivery also retries it. Pending ingress keeps justified control networking available without declaring a fabricated delivery obligation or receipt. Capacity must return before a blocked normal control write can complete. Different auxiliary proof variants have distinct ingress hashes, so rejecting an invalid augmentation cannot overwrite a prior envelope.

Custody cleanup runs only after the existing control handler has a terminal outcome. Validator rejection discards the exact rejected input. The SQL completion predicate independently requires a matching committed applied/quarantined control proof or a permanent deleted-user barrier before removing completed custody. If cleanup itself fails after application committed, authority and real receipts remain committed while the extra fence stays conservative; an exact retry completes cleanup. Existing applied-core replays do not create a new ingress insert/delete pair. Neither ingress cleanup nor completed delivery removes historical authorization, cutoffs, canonical control evidence or permanent barriers.

[SqliteStorageRetentionAnalyzer](../../Common/Backend/Persistence/SqliteStorageRetentionAnalyzer.cs) reports committed collection counts and charged bytes, plus per-record reasons and stored dependency references for all sixteen synchronization-evidence categories. `AnalyzeEvidenceAsync` reads one bounded page per category. `AnalyzePageAsync` supplies immutable-primary-key continuation, including receipt kind/BLOB-hash namespaces, and rejects cursors from another table or user scope. Metadata pages contain at most 64 records; cryptographic categories contain at most eight. Each page uses a consistent read snapshot, without decrypting user content or writing evidence. References identify retention constraints rather than granting new authority. Aggregate reclaimable bytes remain a lower-bound estimate for the analyzed pages, not an exhaustive deletion permit.

### Authenticated evidence compaction

The retention model follows the existing consumers rather than treating a higher revision, age or completed delivery as a universal dominance proof:

| Evidence / immutable identity | Proof and consumers | Retention / supported compaction |
| --- | --- | --- |
| Snapshot: user, original installation, key epoch, revision and hash | Original signature binds encrypted content, membership epoch and merged coverage; inbox, exact anti-entropy requests, username projection, recovery and causal GC use it | One retained row per origin/key namespace is already the normal representation. Keep pending payloads, healthy recovery sources and exact signed merged receipts. Cross-origin merged knowledge is not a replacement envelope. |
| Revision knowledge: user, installation and key epoch | Highest stored revision/hash differs from highest truly merged revision; replay/fork checks, inventories, removal cutoffs and GC depend on both | Already one row per namespace. Preserve all entries and origin boundaries. |
| Membership, control operations and removal cutoffs | Signed addition/removal/key transitions and their exact operation hashes establish historical authors, predecessor authority and accepted revision limits | Keep original signed history and conflict markers. No unsigned local summary replaces authority. |
| Account-deletion barrier: permanent user ID and exact deletion operation | Prevents resurrection after canonical cleanup; delivery retains the historical signing context | Permanent. Compaction never deletes or rewrites barriers. |
| Frozen revocation context: operation ID/hash, original signer and signed recipients | `RevocationDeliveryHistoryUtil.Validate` authenticates the full membership predecessor chain and installation cutoffs; receipt verification and outgoing relay also read the independent proof column | Only byte-equivalent duplicate auxiliary proof copies inside `EnvelopePayload` may be removed. The complete `MembershipProofPayload`, authorization context, signed operation core, signature and recipient declarations stay intact. |
| Application/custody receipts: operation, recipient installation/authorization, kind and attested receipt hash | Original recipient signatures and exact application receipt hashes establish application and final custody; returning peers can request them | Keep exact signed receipts, pending obligations and peer evidence debts. Existing authenticated peer-inventory debt cleanup remains responsible for satisfied debts. Delivery completion alone grants no deletion permission. |
| Fault: affected user/origin/epoch/revision and bad/conflicting hashes | Health gates, terminal conflict detection, recovery scheduling and diagnostics | Keep active and terminal records, including fork/control conflicts. A superseded ordinary origin fault can lose its free-form diagnostic code only after its exact healthy replacement and merged knowledge are authenticated. Its ID, origin, revision, kind, affected component, bad/conflicting hashes, timestamps and recovery identity remain. |
| Canonical checkpoint and protected recovery journal | Signed canonical generation supports rollback detection/reconstruction; a journal can retain the only unresolved historical ciphertext or authority | Keep current checkpoint and unresolved journal. Only the existing verified journal lifecycle may reduce or remove its protected archive. Dormant reset contexts remain unchanged. |
| Origin/version counters and enrollment commits | Local namespace monotonicity, recovery, enrollment retry and revocation authority | Retain existing bounded representations and lifecycle rules. Inactivity, blocking or being offline never establishes origin retirement. |

[RevocationEvidenceCompactionPolicy](../../Common/Backend/Sync/RevocationEvidenceCompactionPolicy.cs) is shared by analysis, maintenance and new/revised revocation writes. It verifies the original operation signature and complete historical membership chain, then compares each removable proof against an exactly serialized retained proof, including original signature, payload, epochs and hash. Auxiliary lists are outside the signed operation core in protocol 16. Missing/mismatching proof, an incomplete chain, receipt conflict or reset-blocked context prevents compaction. Every independently signed proof still exists in the dedicated column; outgoing control relay reconstructs its auxiliary list from that column using the existing verifier. No new statement of historical authority is signed.

[RecoveredOriginFaultCompactionPolicy](../../Common/Backend/Sync/RecoveredOriginFaultCompactionPolicy.cs) supports only superseded `SnapshotOrigin` decrypt/integrity/metadata faults with positive bad and strictly newer recovery revisions, a 32-byte bad-evidence hash, and no blocking flags or conflicting hash. It requires the exact replacement revision as a retained healthy publication/merged receipt, matching highest-stored identity/hash, truly merged knowledge, and historical signature/cutoff verification. The canonical key epoch must still match. Any protected recovery journal, deletion barrier, pending security ingress, active blocking health fault or control conflict retains the original diagnostics. The only changed column is `DiagnosticCode`; replay/fork evidence and the affected component remain exact. Missing proof and terminal conflicts never authorize this reduction.

[SqliteSynchronizationEvidenceCompactor](../../Common/Backend/Persistence/SqliteSynchronizationEvidenceCompactor.cs) captures candidates in a read snapshot before taking lifecycle ownership. Under the shared per-user lock and an immediate SQLite transaction it rereads and byte-compares every context field, repeats the same proof policy, and stages a guarded update of only the duplicated envelope representation or recovered diagnostic text. Commit, cancellation and storage errors follow the normal unit-of-work/savepoint rules. A changed capture is skipped. Each record commits independently; a later failed record does not undo earlier safe commits. Pure representation changes publish no frontend or runtime-reconciliation event. SQLite accounting triggers charge the smaller row atomically, and the existing category, WAL and native file guards remain active even during reclamation.

New/revised revocation contexts normalize during their existing write transaction, reducing admission size before capacity accounting. The existing recovery-journal owner also processes one pending evidence batch per maintenance opportunity, using its existing minute timer. Revocation pages inspect at most eight immutable keys plus a continuation lookahead and materialize at most 16 MiB of public proof payloads. Fault pages inspect at most eight records, verifying bounded existing snapshot envelopes only for eligible faults. Keyset continuation advances past oversized, malformed, reset-blocked, fenced or unverifiable records; they remain charged while later independent evidence can still be considered. Direct `CompactAsync` handles one bounded page of each supported category; the owner uses `CompactBatchAsync` and its cursor to traverse the complete pass.

`SynchronizationEvidenceMaintenanceState` keeps only a constant-space cursor and proof/request generations in memory. Startup/restart and committed revocation or health-proof changes request a pass. Ordinary snapshot identity/status changes only invalidate the completed proof generation; they do not request a history scan on each mutation. Capacity pressure can request one pass for newly changed evidence. Timestamp-only duplicate receipt updates do not dirty that generation. Relevant changes arriving during a pass preserve a follow-up request. Exhausted or failed unchanged generations do not repeatedly restart; a new requested proof generation, changed-evidence capacity event or backend restart supplies the next opportunity. Batches continue only while requested; idle journal ticks perform no evidence query. Commit notifications, including generation changes, occur after the surrounding transaction commits, and rollback publishes none. Representation-only compaction produces no new generation, frontend event, or networking wake-up, so maintenance and GC cannot schedule each other indefinitely.

There is no new timer or admission/compaction retry loop. A capacity refusal still reports failure to the original caller; cleanup occurs separately and never fabricates operation success. Freed pages are reusable; existing bounded incremental-vacuum/WAL maintenance controls physical file lengths.

GC and compaction share lifecycle ownership and SQLite writer serialization. Compaction never removes snapshot receipts, revision knowledge or membership/removal history consumed by GC. The GC refinement is limited to already-merged authenticated ancestors described in [synchronization](SYNCHRONIZATION.md#causal-tombstone-collection). Snapshot/history deletion and receipt/debt summarization beyond the existing authenticated lifecycle rules remain deliberately unsupported: the current protocol supplies no general replacement for these exact historical proofs. Per-record inventory and stored references cover all evidence categories, while destructive eligibility remains limited to the two focused authenticated policies. These records remain charged against Phase 4's finite limits; selective origin retirement is still deferred.


### SQLite files and maintenance limits

[SqliteStorageMaintenance](../../Common/Backend/Persistence/SqliteStorageMaintenance.cs) configures every connection opened through the factory, including pooled reopen and the independent version clock. `max_page_count` is calculated from the actual page size for an 8 GiB main-database limit; an already larger database is refused without deletion. WAL auto-checkpointing uses 1,024 pages. `journal_size_limit` targets 64 MiB of retained WAL/rollback-journal allocation after reset/commit. SQLite temporary storage uses FILE so large sorts may spill instead of requiring the entire temporary working set in RAM. Those spills share the native temporary-file allowance below. Memory mapping is disabled so file extension crosses the guarded I/O interface. Existing synchronization/journal durability settings are preserved.

Deleted pages are reused through SQLite's freelist. New databases enable INCREMENTAL auto-vacuum before creating tables. After successful schema initialization, one batch retires at most 256 unused pages; SQLite relocates live pages without deleting logical records. Busy/capacity failures postpone this optional work. Existing non-incremental images retain their format and reuse their freelist without an automatic full VACUUM. A smaller logical vault can therefore retain allocation up to its finite main-file limit. Existing migrations use fixed temporary and backup paths rather than an accumulating sequence of recovery databases. Recovery journals are rows inside the same guarded vault, not extra per-attempt files.

Legacy encryption conversion runs before normal backend workers start. Its unpooled connections now use the same factory configuration, main-page limit and WAL-pressure function as runtime connections. A source image above 8 GiB is refused before copying. The page-count check also covers committed pages still in WAL, so a small main file does not bypass admission. The page limit is reapplied after rekey using the resulting cipher's actual page size before the staged image can be promoted.

[BoundedDatabaseFileCopy](../../Common/Backend/Persistence/BoundedDatabaseFileCopy.cs) creates the fixed migration stage and backup with `CreateNew`, copying encrypted bytes in 64 KiB chunks up to the admitted source length, capped at 8 GiB per copy. It rejects oversized sources before creating a destination, detects shortening/growth, honors cancellation and flushes completed copies. It cannot overwrite or remove a pre-existing destination after a failed creation. Failed copies remove only their own partial file when possible; failed cleanup leaves the same finite slot for a later verified attempt. Native rekey working files additionally cross the bounded VFS described below.

[DatabaseMigrationStoragePolicy](../../Common/Backend/Persistence/DatabaseMigrationStoragePolicy.cs) requires reported available space of at least four source-image lengths plus 64 MiB before retiring prior migration copies and starting conversion. The estimate allows a staged image, backup and two source-sized rewrite allowances. The probe uses the longest matching reported filesystem mount; inability to obtain space information refuses conversion. This is a preflight estimate, not an allocation reservation. Filesystem routing, concurrent disk consumption and native temporary/journal overhead still require platform validation; a later I/O failure must preserve the original database.

An existing stage or backup may be retired only after the authoritative image passes `quick_check`, contains an application table and checkpoints successfully. A readable header or empty SQLite image is insufficient. The normal already-converted startup path avoids another full scan when no migration artifacts exist; the initializer performs its usual structural check afterward. Backup promotion is verified before moving it, and a replacement for an unreadable main also requires workspace admission. Missing/unreadable main files with ambiguous sidecars, or a missing main with only a stage remaining, refuse automatic replacement and preserve the candidates. The existing explicit reset remains the destructive recovery path. A successful conversion verifies both the copied backup and promoted target before retiring the backup.

[SqliteProviderInitializer](../../Common/Backend/Persistence/SqliteProviderInitializer.cs) initializes SQLite3MC once per process and freezes the selected provider before factory connections open. Backend restart, bounded-VFS registration and test fixtures share this initializer. The provider owns the managed delegates passed to native SQLite; replacing it while existing handles retain callback addresses can collect a live callback and terminate the process. Provider ownership therefore spans host generations and pooled connections. The provider is not reset during database reset or service-host disposal.

[SqliteWalPressure](../../Common/Backend/Persistence/SqliteWalPressure.cs) adds a 1 GiB WAL-pressure admission threshold. Every factory-opened handle, including pooled reopen, registers the non-deterministic `pml_wal_admit()` function using the provider's resolved database filename. BEFORE INSERT/UPDATE/DELETE triggers sample the actual WAL file length inside the SQLite writer statement. At or above the threshold, or when the file size cannot safely be read, the row write fails with the existing capacity error before mutation/accounting. The same guard covers independent clock and critical-ingress writes, immediate repository SQL, cascades, and additional statements in a long transaction after cache spills reach pressure. A file-backed handle without the function cannot execute guarded writes. In-memory fixtures have no WAL file and retain logical admission only.

Newly opened handles and already-open sessions outside their explicit read/write snapshots attempt a TRUNCATE checkpoint only under pressure. One factory coalesces concurrent maintenance probes and throttles them to once per second using a monotonic clock, avoiding repeated file-size queries on ordinary reads. Write admission always samples afresh, independently of that cooldown. SQLite's busy handler is temporarily disabled and restored; provider lock retries have a one-second command timeout. A blocked checkpoint leaves readers and existing evidence intact. A later ordinary retry after readers release can checkpoint and resume writes. There is no maintenance daemon, SQL inside the admission callback, forced reader cancellation, or manual deletion/truncation of live sidecars. Reads remain available while writes are refused. Even deletes and no-growth updates can be refused, since they also require WAL space; duplicate UPSERTs may conservatively fail before conflict resolution.

[SqliteBoundedVfs](../../Common/Backend/Persistence/SqliteBoundedVfs.cs) enforces separate hard file-length limits below SQLite3MC's encryption VFS. Every factory connection selects this stack before opening, including pooled handles, startup DDL, the independent clock, critical ingress and legacy conversion. SQLite3MC retains cipher and native rekey ownership; the platform VFS retains reads, locking, synchronization and file retirement. Unsupported native VFS layouts refuse registration instead of falling back to unbounded writes.

| File class | Hard allowance | Enforcement |
| --- | ---: | --- |
| Main image | 8 GiB per image | `xWrite`/`xTruncate`; page admission cannot exceed this value |
| WAL | 2 GiB per image | Every native write, including spills and commit |
| Main rollback journal | 16 GiB per image | Every native write/truncate; generous allowance for page/sector overhead |
| WAL shared memory | 64 MiB per image | Reject a mapping whose end would exceed the allowance |
| Temporary SQLite files | 2 GiB total, at most 64 simultaneously owned files | Shared serialized reservations across handles using the production VFS; failed/partial writes retain their reservation until proven retirement |

The 1 GiB row-admission threshold remains an early pressure control. An admitted statement or commit cannot extend the WAL beyond the independent 2 GiB hard allowance. WAL admission also accounts conservatively for the finite WAL-index capacity (32 KiB blocks, first block covering 4,062 frames, later blocks 4,096, assuming the minimum 512-byte database page). A reduced shared-memory allowance therefore limits WAL writes before a later commit-index append could require an out-of-range mapping. Optional OS preallocation/chunk-size hints are suppressed, and memory-mapped fetch is disabled. Checks use subtraction before addition to reject invalid/overflowing ranges. Temporary allowances require native delete-on-close; unknown persistent auxiliary classes are refused. A successful close refunds temporary ownership; a failed close conservatively keeps its allowance charged for that process. The single production registration is shared for the process lifetime; reduced test policies are separate and the registration count is finite.

Application code owns three fixed database-image slots: main, migration stage and migration backup. Applying each per-image allowance to all three, plus the shared temporary allowance, gives a deliberately loose finite upper bound of **80 GiB + 192 MiB** for their SQLite file lengths. This includes mutually exclusive journal modes and slots that normally do not coexist. Copy staging obeys the same 8 GiB image size. Auxiliary configuration/log bounds below are separate. Temporary bytes are per backend process; Windows Agent and Android service retain their existing single-backend ownership model. OS allocation granularity, swap, filesystem metadata and files created by other software are outside these file-length bounds.

The shim returns SQLITE_FULL before a disallowed write or extension reaches the platform VFS. The existing capacity error handling keeps that separate from corruption, and SQLite retains transaction/recovery ownership. Already oversized external/legacy artifacts are preserved; opening them does not grant further writes beyond the limit or authorize file deletion. A blocked checkpoint never permits deleting a live WAL/SHM file. The [Phase 4 checklist](STORAGE_PHASE4.md) records implementation and pending platform validation. See SQLite's [file-method interface](https://www.sqlite.org/c3ref/io_methods.html), SQLite3MC's [non-default VFS configuration](https://utelle.github.io/SQLite3MultipleCiphers/docs/faq/faq_overview/), and SQLite's [WAL checkpoint behavior](https://www.sqlite.org/wal.html#checkpointing).

### Auxiliary files and crash retention

Configuration/settings replacement uses one fixed `<filename>.tmp` staging slot per authoritative file. Writers hold an exclusive `<filename>.lock` file across orphan cleanup, staging and rename. The empty lock file remains after release so another owner cannot acquire a different inode for the same lock. The existing preferences lock also serializes separate frontend/Agent store instances. Background settings wait for the same per-file lock with cancellation and a five-second contention deadline; the best-effort firewall flag skips a write when its lock is unavailable. No contender may clean another writer's staging slot.

Before a replacement, the lock owner removes its fixed orphan and former `<filename>.<32-digit GUID>.tmp` orphans. A cleanup failure stops a storage-producing replacement before another file is allocated. Failed rename preserves the preceding authoritative settings; failed final cleanup leaves at most the single staging slot, reused on retry. Other filenames are untouched. The current validated preferences, background-sync and firewall schemas contain only finite enums, integers and booleans; their generated JSON files are each below 1 KiB. This bounds each file's replacement peak to its current copy plus one staging copy, with one empty lock file.

[DbConfigManager](../../Common/Backend/Security/DbConfigManager.cs) additionally caps the entire protected-key configuration, including its header, at 64 KiB before writing or allocating a read buffer. Initializers share the configuration lock and cannot overwrite an already committed key. A missing configuration for an existing database never authorizes generating a replacement key or deleting possible key-recovery files; migration copies/backups or WAL/SHM/journal remnants also block replacement when the main file is missing. Existing key orphans are cleaned only after the configured key opens the database and the initializer passes its structural checks. Unwrapping a key alone is insufficient. If that cleanup fails, the usable vault remains available and no additional key file is allocated. Explicit database reset retains its existing destructive semantics and also removes the fixed staging slot.

[BoundedDebugLogWriter](../../Common/Backend/Diagnostics/BoundedDebugLogWriter.cs) limits recognized backend diagnostic logs across cooperating processes to eight files of at most 4 MiB each (32 MiB total). Entries are UTF-8 bounded to 64 KiB, including a truncation marker. Admission, oldest-file retention and append share one empty `backend-debug.lock` file; lock contention or cleanup/I/O failure drops diagnostics instead of growing another file or failing an application operation. Oversized legacy logs are removed; ordinary old timestamp-named logs participate in the same count/byte allowance. Unrelated files are preserved. A successful append establishes these limits even after upgrading from the former unbounded logger. Interrupted retention can lose diagnostic history but never touches vault or recovery evidence. Release backend logging remains disabled.

These limits describe file lengths and application-created file counts, separate from the vault-row budget. Migration copies have finite admission and workspace preflight; native rekey, WAL/SHM, rollback and temporary-file writes use the bounded VFS. Free-space preflight remains an estimate rather than a disk reservation. External file modification, operating-system swap and other processes are outside the application-controlled guarantee. Native interop, encrypted reopen/rekey and failure behavior still require the documented Windows/Android validation before release.

Database format 18 retains WAL-pressure admission, logical budgets and critical-ingress custody, and adds bounded competing fork envelopes with evidence that survives local profile erasure. Earlier development database versions require explicit recreation through the existing reset flow. Synchronization protocol 17 and enrollment payload 8 bind signed fork-exclusion evidence; see [recovery](RECOVERY.md#evidence-format-and-storage). [Storage tests](../Testing/MULTI_DEVICE_SYNC_STRESS.md#storage-capacity-tests) cover logical admission, physical exhaustion, bounded compaction and evidence-preserving failure paths. The VFS and INCREMENTAL auto-vacuum change no application tables, signed envelope formats or synchronization semantics.

## Exceptional recovery presentation

Shared Windows/Android views keep structural database recovery separate from ordinary connectivity retries, authenticated terminal synchronization blockers, confirmed profile-history repair and removal of a disconnected local profile. Verified automatic canonical recovery remains unobtrusive. Terminal conflicts preserve the existing fail-closed policy; there is no UI choice between histories. Device attribution and available actions still come from existing authenticated backend results.

Database reset has a scrollable warning, an initially unchecked acknowledgement, dedicated confirmation controls, a non-destructive retry, keyboard focus on a safe action and accessible control names. Confirmed history repair retains its plan fingerprint, master-password verification and fresh acknowledgement. Changed credentials invalidate the acknowledgement. Partial commits clear the old repair offer and explain that follow-up verification must be reviewed; unknown transport outcomes are not presented as clean rollback. English/Hungarian guidance distinguishes local deletion, remote signed disconnection and explicit fresh enrollment. See [recovery](RECOVERY.md#structural-database-recovery).

## Frontend state

The backend publishes scoped, sequenced change notifications. Windows carries them over IPC; Android delivers them through the authorized activity attachment. [FrontendRefreshCoordinator](../../Common/Frontend/Services/FrontendRefreshCoordinator.cs) coalesces refreshes, handles missed notifications/runtime replacement, and preserves in-progress edits. Device views consume cached presence; backend discovery/probing owns network work.

[ApplicationPreferences](../../Common/Contracts/Preferences/ApplicationPreferences.cs) stores language, theme, and presentation settings. The frontend applies localization/theme per UI session. The Agent reads its language independently for tray text. Presentation preferences, background configuration, and vault state remain separate stores.
