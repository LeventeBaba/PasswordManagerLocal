# Multi-device synchronization stress test

The five-device regression test is in:

`Common/Tests/Backend/Services/MultiDeviceConvergenceStressTests.cs`

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
