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

Partial test classes are grouped by behavior under `AccountLifecycle`, `Adversarial`, `FailureInjection`, `Recovery`, `Relay`, `RevocationDelivery`, `Runtime`, and `Storage`. Each file retains the matching primary type name. SDK default compile inclusion discovers those files without explicit project entries.

## Storage baselines

The `DatabaseGrowthBaseline` category uses [SqliteStorageMeasurement](../../Common/Tests/TestInfrastructure/Storage/SqliteStorageMeasurement.cs) to capture main/WAL/SHM/journal lengths, page/freelist counts, journal mode, every persistent table's row count, and aggregate BLOB lengths. Explicit PASSIVE checkpoint samples record busy/log/checkpointed-frame status. Output contains counts and lengths rather than plaintext vault data or keys.

Workloads cover unchanged synchronization, repeated discovery, bounded live-data edits/remote merges, receipt/custody exchanges, enrollment/removal, recovery attempts, rejected authentication/malformed envelopes, backend restart, control anti-entropy, and causal tombstone collection. No-op/admission cases assert idempotence; fixed-origin cases check applicable snapshot row bounds. Membership churn retains signed history rather than deleting evidence to meet a file-size assertion.

```powershell
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --filter 'TestCategory=DatabaseGrowthBaseline&TestCategory!=SyncStressExtended' --logger 'console;verbosity=normal' --logger 'trx;LogFilePrefix=storage-baseline'
dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj -c Debug --no-build --no-restore --environment 'PASSWORDMANAGERLOCAL_SYNC_STRESS=1' --filter 'TestCategory=DatabaseGrowthBaseline' --logger 'trx;LogFilePrefix=storage-baseline-complete'
```

Stable file size or row count does not establish a lifetime storage bound. BLOBs can grow within fixed rows; SQLite can reuse free pages or retain allocated space after deletion. Connection closure can checkpoint/remove WAL sidecars. Interpret row/BLOB totals, allocated/used-page estimates, and sidecar/checkpoint samples together.

## Physical-device coverage

In-process transport and controlled runtime services do not reproduce OS socket timing, Wi-Fi multicast, platform scheduling, force-stop, or installer behavior.

| Platform | Physical checks |
| --- | --- |
| Windows | Sign-in startup; frontend closure/reopen with one Agent; enabled idle; last-peer disable/re-enable/block/unblock; offline peer return; sleep/resume; interface/firewall changes; Agent restart; upgrade; revocation-only delivery; final receipt with another eligible profile; retry/restart status |
| Android | Foreground notification/readiness; activity closure/replacement; enabled idle and local reactivation; screen lock/sleep; Wi-Fi/router multicast; reboot/first unlock; supported service restoration; notification/start restrictions; battery/OEM limits; revocation-only delivery; remembered-key recovery; force-stop behavior |

Passing the seeded cases establishes behavior for those scenarios. It does not establish universal convergence across every interleaving or guarantee platform restoration when the operating system forbids execution.
