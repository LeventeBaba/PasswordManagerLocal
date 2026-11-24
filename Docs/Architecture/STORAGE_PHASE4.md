# Phase 4: bounded storage

Implementation is complete for review. Builds, automated tests and physical-device validation are pending; none were run during this completion pass.

| Requirement | Implementation |
| --- | --- |
| Finite lifetime growth, including histories that cannot safely be collected | Installation-wide category budgets, positive per-row charges, individual row/stream limits and profile limits |
| Preserve deletion, authorization, replay/fork, custody and recovery evidence | Existing authenticated lifecycle/causal cleanup only; unresolved or permanent records stay charged and fail closed at capacity |
| Stop a transaction that outgrows early WAL admission | Native bounded VFS below SQLite3MC; guards every file write/truncate, including spills and commit |
| Bound main, journal, shared-memory and native rekey working files | Main/WAL/rollback/SHM caps; finite shared temporary bytes and file count; every factory connection selects the bounded encrypted VFS before open |
| Avoid allocation paths that bypass byte admission | Suppress preallocation/chunk hints and mmap; shared temporary reservations include failed/partial writes |
| Safe physical reclamation | Reuse freelist pages; new images support one bounded incremental-vacuum batch at startup; preserve older formats without automatic full rewriting |
| Finite copies and interrupted-maintenance slots | Fixed migration stage/backup paths, bounded copies, verified promotion/retirement, workspace preflight; native rekey failure leaves the original authoritative image intact |
| Finite auxiliary files | Fixed settings/key staging slots and locks; configuration size admission; existing bounded Debug logs |
| Regression coverage | Logical/category exhaustion, real SQLite FULL, no false receipts/knowledge, pinned readers, native WAL/journal/SHM/temp exhaustion, encrypted reopen/rekey, failed migration/retry and evidence-preserving compaction |

See [Application architecture](APPLICATION.md#storage-admission-and-limits) for the complete table inventory, byte budgets and file-length accounting. [Storage testing](../Testing/MULTI_DEVICE_SYNC_STRESS.md#storage-capacity-tests) contains focused test commands.

The backend uses the existing SQLite3MC package and native library. The VFS itself requires no format change. Current database format 18 and synchronization protocol 17 additionally retain and bind signed fork-exclusion evidence; see [recovery](RECOVERY.md#evidence-format-and-storage). Native VFS structs/callbacks require the supported platform's version-3 OS VFS. Missing/unsupported registration fails closed. The interop uses unmanaged Cdecl callbacks suitable for Native AOT, and the existing packaged `sqlite3mc` binary; no additional native binary is introduced.

Release validation must include the complete solution build/tests, the opt-in bounded-growth and signed synchronization stress suites, Windows Native AOT publish/reopen, and Android Release launch/reopen with an encrypted vault. Exercise ordinary synchronization, a blocked-reader capacity failure followed by retry, and legacy encryption conversion on the supported native packages. Those platform outcomes cannot be inferred from source review.

Limits apply to application-controlled file lengths, separately from OS allocation units, swap and filesystem metadata. Existing oversized or externally changed artifacts are preserved and may require explicit recovery; capacity failure never grants permission to reset a vault or discard unresolved evidence. Preflight free space is an estimate rather than a reservation. Reaching a finite evidence budget can require stopping further admissions until existing safe cleanup can progress.
