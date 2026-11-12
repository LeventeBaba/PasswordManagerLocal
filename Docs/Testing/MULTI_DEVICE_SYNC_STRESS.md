# Multi-device synchronization stress test

The five-device regression test is in:

`Common/Tests/Backend/Services/MultiDeviceConvergenceStressTests.cs`

Both stress-test classes share the production-backed host and synchronization
helpers in `Common/Tests/Backend/Services/MultiDeviceSyncStressFixture.cs`.
Each source file defines exactly one matching primary type, as required by the
architecture guard. The opt-in categories and test behavior remain unchanged.

This test uses five independently enrolled production backend instances, each with its own
SQLite database, repository layer, encrypted snapshots, version clocks, signed operations,
queues, and protocol handlers. Only the operating-system network transport is replaced
with the repository's existing in-process authenticated transport.

## Running

The test is opt-in to keep everyday test runs fast. After building the project yourself,
run from the repository root:

```cmd
 dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj --no-build --environment "PASSWORDMANAGERLOCAL_SYNC_STRESS=1" --filter "TestCategory=SyncStress"
```

If not built yet, omit `--no-build`. The test runs two fixed replayable random seeds,
`1701` and `20261111`. A failure includes the seed, the step, active graph edges, and
per-device membership IDs, signed control-operation epochs and statuses, fault diagnostics, durable queues, and transport failures. For a single-seed reproduction, temporarily keep
only the applicable `[DataRow(...)]` on the test method.

## What is exercised

1. Sequential enrollment of five separate devices. Every new pair explicitly enables local sync and completes authenticated control-inventory/queue exchange before the next enrollment. Signed membership still must propagate to all existing devices over the chain before editing starts.
2. Offline independent password additions on all five devices, repeated across four
   changing partitions with randomized write order and session direction.
3. Concurrent edits to one existing password, and an edit-vs-delete race on another.
4. Password tags and custom colors created independently on different devices.
5. Multi-hop forwarding from device 0 to device 4, with no direct network link.
6. Injected failed delta transfer, durable queue retention, and retry propagation.
7. Restart of device 2 from its persistent database while disconnected, followed by
   a new edit and reconnection.
8. Eventual propagation through a chain, then full-mesh anti-entropy and queue drain.
9. Agreement on logical password IDs, metadata, hashes of decrypted secrets, tags, and
   custom colors, plus preservation of every uniquely created password.

## Limits and interpretation

The test does not simulate actual TCP/UDP packet timing, Android process suspension,
clock jumps, or arbitrary Byzantine devices. It establishes deterministic agreement
for the tested schedules; it does not prove universal convergence for every possible
combination of concurrent operations. Conflict resolution may legitimately choose
one of two competing updates, or a deletion over an update; the test verifies that
all replicas agree and unrelated changes are preserved.

Do not remove a failing seed or increase retry limits simply to make a failure pass.
Use the recorded seed, schedule, queue state, and snapshots to investigate first.


## Extended adversarial churn test (separate opt-in category)

The additional test is in
`Common/Tests/Backend/Services/MultiDeviceAdversarialChurnStressTests.cs`.
It reuses the same production-backed five-device test fixture and is considerably
slower than the original two-seed test. Run it separately **after building**:

```cmd
 dotnet test .\Common\Tests\PasswordManagerLocal.Common.Tests.csproj --no-build --environment "PASSWORDMANAGERLOCAL_SYNC_STRESS=1" --filter "TestCategory=SyncStressExtended"
```

It uses the reproducible operation/topology seeds `271828` and `314159`, with a
20-minute per-case timeout. Seeds fix the intended operation order and graph
schedule, not operating-system thread scheduling. Use a filter such as
`--filter "FullyQualifiedName~FiveDevices_StaleReplicaQueueLossAndRepeatedChurn_ConvergeWithoutResurrection&TestCategory=SyncStressExtended"`
to run just this test method (both seeds). To isolate one seed, temporarily
comment out the other `[DataRow]`; retain both in the committed source.

### New coverage (not simply more repetitions of the baseline test)

1. Keep a **long-lived fifth peer fully disconnected** while the other four
   repeatedly partition, perform 12 waves of independent writes and competing
   edits, exchange snapshots over changing topologies, and restart relays.
2. Delete an item on the active side; prove the four active replicas agree on
   the tombstone while the fifth still carries the old live version.
3. Create another publication on the stale peer, erase **all its pending
   outgoing queue entries**, restart the peer, and reconnect it through a single
   link. Its new data must still propagate through **snapshot anti-entropy**,
   while the old deleted item must not be resurrected.
4. Require the five devices to reach canonical logical agreement and drain
   pending queues in a full mesh. Verify the **exact decrypted secret bytes** of
   every independently created password on every device, not merely its name.
5. After this first convergence, partition **all five devices again**. Make
   additional independent writes plus five competing edits to the same password,
   restart a relay, and converge over a different multi-hop chain.
6. Require a second full-mesh quiescence, no deleted-item resurrection, no lost
   independent secret, and stability after redundant anti-entropy exchanges.

This is an intentionally adversarial recovery test. It deliberately erases
**queue bookkeeping** through the existing test-host helper; it does **not**
delete encrypted snapshots or modify production synchronization code. The
expected behavior is recovery through the production anti-entropy protocol.

### Troubleshooting

The test logs the seed, wave, reachable edges, writers, restart, and queue-loss
count. `HealUntilAsync` includes per-device membership epochs, operations,
transport failures, pending queues, and password names when a convergence
checkpoint fails. Do not reduce the assertions, remove a failing seed, or
silently raise the healing-pass limit. First determine whether the fault is in
simulated scheduling, recovery logic, merge semantics, or the test's assumptions.

As with the original stress test, this does **not** simulate real Wi-Fi, Android
service suspension, arbitrary clock skew, or every possible interleaving. Passing
both seeds is valuable evidence, but cannot prove universal convergence.
