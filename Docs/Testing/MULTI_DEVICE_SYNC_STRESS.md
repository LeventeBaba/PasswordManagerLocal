# Multi-device synchronization tests

The synchronization suites use production services with isolated SQLite vaults, encrypted snapshots, signed authority, revision knowledge, queues, and protocol handlers. [ProductionSyncTestHost](../../Common/Tests/TestInfrastructure/ProductionSyncTestHost.cs) replaces OS transport with authenticated in-process transport. [MultiDeviceSyncStressFixture](../../Common/Tests/Backend/Services/MultiDeviceSyncStressFixture.cs) shares enrollment, topology, restart, healing, and convergence helpers across five-device scenarios.

## Opt-in scenarios

Expensive cases require `PASSWORDMANAGERLOCAL_SYNC_STRESS=1`. Normal suite filters exclude `SyncStress` and `SyncStressExtended`.

| Suite / scenario | Category | Seeds / setup | Coverage |
| --- | --- | --- | --- |
| [MultiDeviceConvergenceStressTests](../../Common/Tests/Backend/Services/MultiDeviceConvergenceStressTests.cs) | `SyncStress` | `1701`, `20261111`; five devices | Partitions, concurrent writes, edit/delete races, tags/colors, relay, failed transfer/retry, persistent restart, chain/mesh convergence |
| [MultiDeviceRuntimeEligibilityStressTests](../../Common/Tests/Backend/Services/MultiDeviceRuntimeEligibilityStressTests.cs) | `SyncStress` | `20261115`, `7719`; five devices | Offline eligibility, peer disable/re-enable, duplicate wake-ups, controlled-service failure/retry, persisted restart decisions |
| [MultiDeviceAdversarialChurnStressTests](../../Common/Tests/Backend/Services/MultiDeviceAdversarialChurnStressTests.cs) | `SyncStressExtended` | `271828`, `314159`; five devices | Stale fifth replica, 12 write/partition waves, relay restart, queue loss, anti-entropy recovery, second partition/convergence, no resurrection |
| [LockedRelayRestart](../../Common/Tests/Backend/Services/Relay/RegularSyncEndToEndTests.cs) | `SyncStressExtended` | Three-device scenario | Pending signed-envelope custody across locked relay restart, indirect merge, and merge after unlock |
| [EnrollmentRemovalCycles](../../Common/Tests/Backend/Services/Storage/DatabaseGrowthBaselineTests.cs) | `SyncStressExtended`, `DatabaseGrowthBaseline` | Four enrollment/removal cycles | Bounded live membership with retained historical-authority measurements |
| [BoundedStorageGrowthStressTests](../../Common/Tests/Backend/Persistence/BoundedStorageGrowthStressTests.cs) | `SyncStressExtended`, `BoundedGrowth` | Seed `1701`; reduced 32 KiB category budgets | Structural history exhaustion and repeated refusal across snapshot/origin, control/membership, revocation/debt and recovery/fault collections |

Five-device cases enroll independent installations; databases are not cloned. Enrollment preflight propagates signed membership before partitioning. Reachable edges are explicit. Queue-loss scenarios remove queue bookkeeping while retaining the durable signed snapshots needed for anti-entropy.

Convergence assertions compare logical IDs, metadata, secrets, tags/colors, deletion state, and queue drain as applicable. Independent writes must survive; competing versions can resolve to one deterministic winner. Seeds fix intended operations/topologies, not thread scheduling. Five-device convergence/runtime cases allow 12 minutes per seed; extended churn allows 20 minutes per seed.

## Commands

Commands use PowerShell from the repository root after a Debug build. `--environment` enables stress only in test hosts.

```powershell
# Both five-device categories, including extended relay/membership cases.
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=SyncStress|TestCategory=SyncStressExtended' --logger 'trx;LogFilePrefix=sync-stress'

# Standard convergence and runtime eligibility.
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=SyncStress'

# Extended adversarial churn, locked relay, and membership measurement.
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=SyncStressExtended'

# One class, retaining all of its declared data rows.
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'FullyQualifiedName~MultiDeviceAdversarialChurnStressTests'
```

The fixed seeds and healing-pass limits are part of regression coverage. Failures identify the seed, step/wave, topology, device, authority state, queue state, and transport observations. Investigate that state before changing retry ceilings or expected convergence.

## Supporting regression suites

| Area | Test suites |
| --- | --- |
| Committed eligibility and lifecycle | `SyncRuntimeEligibilityTests`, `SyncRuntimeCommittedTransitionTests`, `SyncRuntimeServiceTests`, `SyncRuntimeResilienceTests` |
| Worker/network behavior | `DeviceSyncTaskServiceTests`, `NetworkSignatureChangeTrackerTests`, `SyncNetworkRefreshExecutionProfileTests`, `LocalDiscoveryNetworkLeaseTests` |
| Revocation and deletion | `RevocationReceiptServiceTests`, account-lifecycle and revocation partials of `RegularSyncEndToEndTests`, interrupted-export scenario |
| Recovery durability/concurrency | `UserDataRecoveryCoordinatorTests`, `AutomaticRecoveryTriggerTests`, `UserRecoverySessionServiceTests`, lifecycle and tombstone suites |
| Android ownership/restoration | `AndroidRuntimeServiceHostTests`, `AndroidBackgroundRestorationPolicyTests`, `AndroidServiceStartStateMachineTests` |
| Windows ownership/IPC | `WindowsAgentBackendRuntimeOwnerTests`, `WindowsBackgroundSyncCoordinatorTests`, Windows IPC and Endpoint RPC suites |

```powershell
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~SyncRuntime|FullyQualifiedName~DeviceSyncTaskServiceTests|FullyQualifiedName~NetworkSignatureChangeTrackerTests'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~RevocationReceiptServiceTests|FullyQualifiedName~FinalDeletionReceipt_RestartBeforeExport|FullyQualifiedName~UserDataRecoveryCoordinatorTests'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~AndroidRuntimeServiceHostTests|FullyQualifiedName~AndroidBackgroundRestorationPolicyTests|FullyQualifiedName~AndroidServiceStartStateMachineTests'
dotnet test .\Windows\Tests.IPC\PasswordManagerLocal.Windows.Tests.IPC.csproj -c Debug --no-build --no-restore
dotnet test .\Windows\Tests.EndpointRpc\PasswordManagerLocal.Windows.Tests.EndpointRpc.csproj -c Debug --no-build --no-restore
```

## Fixture ownership and deterministic boundaries

`ProductionSyncTestHost` owns its post-login consumer, protocol workers, temporary database directory, and service provider. Restart preserves the on-disk vault/installation identity and creates a new host. Disposal joins owned work before releasing services/files.

`ControlledDelayTimeProvider` gates one-shot supervisor delays. Explicit signals/gates control startup, shutdown, retry, and recovery fault injection. Production app code has no test fault switches.

Initial-read gate release makes deferred post-login reconciliation runnable; it does not join the consumer. Tests measuring unchanged canonical state during rejected admission use the existing per-user lifecycle coordinator around the baseline, requests, and readback. Nested inbox calls remain reentrant and execute real transactions. Ordinary transfer idleness alone does not establish canonical-write idleness.

Partial test classes are grouped by behavior under `AccountLifecycle`, `Adversarial`, `FailureInjection`, `Recovery`, `Relay`, `RevocationDelivery`, `Runtime`, `Storage`, and `StorageSecurity`. Each file retains the matching primary type name. SDK default compile inclusion discovers those files without explicit project entries.

## Storage baselines

The `DatabaseGrowthBaseline` category uses [SqliteStorageMeasurement](../../Common/Tests/TestInfrastructure/Storage/SqliteStorageMeasurement.cs) to capture main/WAL/SHM/journal lengths, page/freelist counts, journal mode, every persistent table's row count, and aggregate BLOB lengths. Explicit PASSIVE checkpoint samples record busy/log/checkpointed-frame status. Output contains counts and lengths rather than plaintext vault data or keys.

Workloads cover unchanged synchronization, repeated discovery, bounded live-data edits/remote merges, receipt/custody exchanges, enrollment/removal, recovery attempts, rejected authentication/malformed envelopes, backend restart, control anti-entropy, and causal tombstone collection. No-op/admission cases assert idempotence; fixed-origin cases check applicable snapshot row bounds. Membership churn retains signed history rather than deleting evidence to meet a file-size assertion.

```powershell
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=DatabaseGrowthBaseline&TestCategory!=SyncStressExtended' --logger 'console;verbosity=normal' --logger 'trx;LogFilePrefix=storage-baseline'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=DatabaseGrowthBaseline' --logger 'trx;LogFilePrefix=storage-baseline-complete'
```

Stable file size or row count does not establish a lifetime storage bound. BLOBs can grow within fixed rows; SQLite can reuse free pages or retain allocated space after deletion. Connection closure can checkpoint/remove WAL sidecars. Interpret row/BLOB totals, allocated/used-page estimates, and sidecar/checkpoint samples together.

## Storage capacity tests

The same measurement helper also captures charged category usage, category capacities, the main-page ceiling, auto-checkpoint configuration and retained-journal target. The [admission inventory](../Architecture/APPLICATION.md#storage-admission-and-limits) defines their meaning, hard native limits and platform-validation scope.

| Category / suite | Assertions |
| --- | --- |
| `StorageBudget` / `SqliteStorageBudgetTests` | Every application table has insert/update/delete guards; finite row sizes and profile count; byte growth and UTF-8 accounting; savepoint/outer rollback; no failed-flush notifications; no-growth upsert; deletion/cascade releases; restart does not reset usage; conservative analyzer preserves barriers |
| `StorageBudget` / service regressions | Valid signed snapshot rejected at capacity cannot advance knowledge or alter canonical data; exhausted revocation reserve preserves pending deletion/barrier without an applied receipt; local mutations retain the preceding committed vault/checkpoint |
| `SecurityIngress` | Failed initial control admission retains the exact received envelope and blocks affected-profile access across reopen/restart; no author verification before custody when the ingress budget is full; duplicates stabilize; invalid signatures release their temporary fence; completion requires durable proof; post-commit cleanup failure/retry; prerequisite epoch bounds and control-scope retirement; unaffected-profile access and real receipt/barrier recovery |
| `StorageBudget` / codec and AES tests | Exact stream limits, repeated reads/writes and trailing input; forged oversized frame header rejected before renting buffers |
| `StoragePhysical` | Per-open configuration, competing logical writers, actual SQLITE_FULL rollback and freelist reuse; native commit overshoot/pinned-reader WAL limits; rollback-journal and shared-memory limits; shared temporary-file allowance and release; encrypted pooled reopen/rekey; bounded incremental reclamation preserves evidence; older images avoid automatic rewrite |
| `WalPressure` (also `StoragePhysical`) | Pinned-reader write refusal and stable rejected retries across connection/factory reopen; admission on every table and accounting path; independent-clock failure; already-open session retry and notification behavior; forced spills within one transaction and rollback; incomplete schema/unconfigured-handle rejection; encrypted URI filenames and pooled reopen |
| `MigrationStorage` (also `StoragePhysical`) | Exact/oversized copy admission, existing-slot preservation, cancellation and changed input length; workspace refusal and retry; structural verification before cleanup/promotion; empty images and ambiguous recovery artifacts; fixed-copy counts across restarts; blocked checkpoint preservation; WAL-resident pages cannot bypass the main-image limit; native rekey workspace/journal exhaustion preserves the original and permits a later retry |
| `StorageAuxiliary` | Repeated replacement/cleanup failures reuse one settings slot across store recreation; committed values survive; legacy orphan cleanup preserves unrelated files; cancellation cannot remove another writer's slot; physical competing settings/key initializers; key-recovery files survive unavailable/invalid configuration; protected-key size rejection; finite debug-log bytes/count across restarts and competing writers, UTF-8 truncation and lock contention |
| `BoundedGrowth` | Finite reduced budgets, then repeated rejected admissions without additional persistent rows or charges; structural rows test admission independently of cryptographic authorization |

```powershell
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=StorageBudget' --logger 'trx;LogFilePrefix=storage-budget'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=SecurityIngress' --logger 'trx;LogFilePrefix=security-ingress'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=StoragePhysical' --logger 'trx;LogFilePrefix=storage-physical'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=WalPressure' --logger 'trx;LogFilePrefix=wal-pressure'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=MigrationStorage' --logger 'trx;LogFilePrefix=migration-storage'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=StorageAuxiliary' --logger 'trx;LogFilePrefix=storage-auxiliary'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=BoundedGrowth' --logger 'trx;LogFilePrefix=bounded-growth'
```

The original pinned-reader test demonstrates that PASSIVE checkpointing leaves frames outstanding. The WAL-pressure suite uses reduced thresholds and a controlled monotonic clock to exercise refusal, throttled maintenance and resumption without sleeps. The spill workload uses a small SQLite page cache in its isolated connection. These pressure tests assert stable rejected retries and transactional state preservation. The separate `SqliteBoundedVfsTests` deliberately set the pressure threshold above a reduced hard WAL limit and exercise native commit/spill refusal, rollback preservation and resumption after a reader gap. They also exercise rollback journals, shared memory, competing temporary handles, encrypted rekey and incremental page reclamation. The existing measurement helper records the pressure allowance alongside actual WAL/main/SHM lengths and logical budgets.

Reduced budgets and pressure thresholds apply only to isolated test factories. Production defaults remain generous. Auxiliary-file tests use injected replacement/cleanup failures and seeded interrupted staging files, plus physical lock/concurrent-writer tests; they do not simulate a machine power loss or establish filesystem rename durability on every platform. Structural admission tests do not establish correctness for every signed churn/recovery interleaving; the existing authenticated synchronization, revocation, recovery, fork, cutoff and prerequisite suites remain part of release validation. The critical-ingress tests cover the initial control-write and post-application cleanup boundaries; the native VFS tests cover physical refusal separately. The rotating diagnostic writer is tested directly in Debug and Release, while its production call sites remain Debug-only.

Migration tests use small encrypted databases and internal reduced image limits/free-space probes. Deliberately violated CHECK constraints preserve a readable header while causing structural verification to fail, without modifying arbitrary encrypted bytes. Existing stage/backup files must survive those failures. Zero-byte and allocated-but-empty images cannot authorize dropping a healthy staged candidate. The copy tests use bounded streams for cancellation and length-change boundaries; they do not require multi-gigabyte fixture files. The native-rekey exhaustion regression uses reduced native WAL, journal and temporary allowances; repeated failure must preserve the exact original before a successful retry with normal allowances. No test creates a filesystem free-space reservation. Native ABI and actual power-loss/rename durability still require Windows/Android release validation. No build or tests were executed while completing this implementation.

## Physical-device coverage

In-process transport and controlled runtime services do not reproduce OS socket timing, Wi-Fi multicast, platform scheduling, force-stop, or installer behavior.

| Platform | Physical checks |
| --- | --- |
| Windows | Sign-in startup; frontend closure/reopen with one Agent; enabled idle; last-peer disable/re-enable/block/unblock; offline peer return; sleep/resume; interface/firewall changes; Agent restart; upgrade; revocation-only delivery; final receipt with another eligible profile; retry/restart status |
| Android | Foreground notification/readiness; activity closure/replacement; enabled idle and local reactivation; screen lock/sleep; Wi-Fi/router multicast; reboot/first unlock; supported service restoration; notification/start restrictions; battery/OEM limits; revocation-only delivery; remembered-key recovery; force-stop behavior |

Passing the seeded cases establishes behavior for those scenarios. It does not establish universal convergence across every interleaving or guarantee platform restoration when the operating system forbids execution.
