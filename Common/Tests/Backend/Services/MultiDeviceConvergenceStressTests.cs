using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Requests;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

/// <summary>
/// Exercises the real production synchronization composition using five independent SQLite
/// databases and the in-process authenticated protocol transport. The only simulated part is
/// network reachability. A fixed seed makes topology and operation ordering reproducible.
/// This deliberately avoids assuming when a particular sync will finish or which concurrent
/// edit wins: every replica must ultimately agree and independent edits must survive.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MultiDeviceConvergenceStressTests : MultiDeviceSyncStressFixture
{
    [TestMethod]
    [DataRow(1701)]
    [DataRow(20261111)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    [TestCategory("SyncStress")]
    public async Task FiveDevices_PartitionedMultiWriterChanges_EventuallyConverge(int seed)
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run multi-device synchronization stress tests.");

        var random = new Random(seed);
        var expectedIndependentEntries = new HashSet<string>(StringComparer.Ordinal);
        Trace(seed, "Starting five-device production-backed convergence run");
        await using var setup = await CreateFiveDeviceSetupAsync(seed);

        // Every device must know all five authenticated memberships before we partition them.
        setup.SetLinks(Chain);
        await HealUntilAsync(setup, seed, Chain, async () =>
        {
            foreach (var device in setup.Devices)
            {
                var members = await device.Host.Endpoints.GetUserDevicesAsync(device.Token);
                if (members.Select(member => member.DeviceId).Distinct().Count() != DeviceCount)
                    return false;
            }
            return true;
        }, "enrollment membership propagation");

        var initial = await ReadAsync(setup.Devices[0]);
        var sharedId = initial.Passwords.Single(x => x.Name == "Stress shared credential").Id;
        var deletionId = initial.Passwords.Single(x => x.Name == "Stress edit/delete credential").Id;
        foreach (var device in setup.Devices)
        {
            var data = await ReadAsync(device);
            Assert.AreEqual(sharedId, data.Passwords.Single(x => x.Name == "Stress shared credential").Id);
            Assert.AreEqual(deletionId, data.Passwords.Single(x => x.Name == "Stress edit/delete credential").Id);
        }

        // The first two partitions cannot communicate. Writes made on all five devices
        // accumulate in separate histories; only a few local links are synchronized.
        for (var wave = 0; wave < 4; wave++)
        {
            (int A, int B)[] links = wave switch
            {
                0 => [(0, 1), (1, 2), (3, 4)],
                1 => [(0, 1), (2, 3)],        // 4 is isolated
                2 => [(1, 2), (2, 3), (3, 4)], // 0 is isolated
                _ => [(0, 2), (2, 4), (4, 1)] // 3 is isolated
            };
            setup.SetLinks(links);
            Trace(seed, $"Wave {wave}: active edges={FormatLinks(links)}");

            foreach (var index in Shuffle(Enumerable.Range(0, DeviceCount), random))
            {
                var name = $"Stress {seed} device {index} wave {wave}";
                await AddAsync(setup.Devices[index], name, $"secret-{seed}-{index}-{wave}-!", $"offline write {wave}");
                expectedIndependentEntries.Add(name);
                Trace(seed, $"Wave {wave}: device {index} created {name}");
            }

            if (wave == 0)
            {
                // The same shared item is edited independently on two disconnected replicas.
                // The winner is decided by the production version-stamp comparer, not by this test.
                await setup.Devices[0].Host.Endpoints.UpdatePasswordAsync(setup.Devices[0].Token,
                    new UpdatePasswordRequest
                    {
                        Id = sharedId,
                        Description = $"conflict-left-{seed}",
                        Password = Encoding.UTF8.GetBytes($"conflict-left-secret-{seed}")
                    });
                await setup.Devices[4].Host.Endpoints.UpdatePasswordAsync(setup.Devices[4].Token,
                    new UpdatePasswordRequest
                    {
                        Id = sharedId,
                        Description = $"conflict-right-{seed}",
                        Password = Encoding.UTF8.GetBytes($"conflict-right-secret-{seed}")
                    });
                Trace(seed, "Offline concurrent edits on the same credential (devices 0 and 4)");
            }
            if (wave == 1)
            {
                // An edit-versus-delete conflict is deliberately left unresolved until healing.
                await setup.Devices[1].Host.Endpoints.RemovePasswordsAsync(
                    setup.Devices[1].Token, [deletionId]);
                await setup.Devices[4].Host.Endpoints.UpdatePasswordAsync(setup.Devices[4].Token,
                    new UpdatePasswordRequest
                    {
                        Id = deletionId,
                        Description = $"edited-while-other-replica-deleted-{seed}"
                    });
                Trace(seed, "Offline delete/edit race on the same credential (devices 1 and 4)");
            }
            if (wave == 2)
            {
                // Different model families must also be merged, not just passwords.
                for (var index = 0; index < DeviceCount; index++)
                {
                    await setup.Devices[index].Host.Endpoints.AddPasswordTagAsync(
                        setup.Devices[index].Token,
                        new NewPasswordTagRequest
                        {
                            Name = $"Stress tag {seed} {index}",
                            Color = $"#FF{index + 1:X2}6184"
                        });
                    await setup.Devices[index].Host.Endpoints.AddCustomUserColorsAsync(
                        setup.Devices[index].Token,
                        [new NewCustomUserColorRequest
                        {
                            ColorName = $"Stress color {seed} {index}",
                            ColorCode = $"#FF{index + 1:X2}84A6"
                        }]);
                }
            }

            // Only selected available edges carry traffic; no forced full convergence yet.
            foreach (var edge in Shuffle(links, random))
            {
                if (random.Next(2) == 0)
                    await KickAsync(setup.Devices[edge.A], setup.Devices[edge.B]);
                else
                    await KickAsync(setup.Devices[edge.B], setup.Devices[edge.A]);
            }
        }

        // Demonstrate store-and-forward without ever connecting the endpoints directly:
        // device 0 writes while only adjacent links in the five-device chain are available.
        setup.SetLinks(Chain);
        var relayName = $"Stress {seed} indirect relay";
        await AddAsync(setup.Devices[0], relayName, $"relay-secret-{seed}-!", "from device 0 via 1, 2, 3");
        expectedIndependentEntries.Add(relayName);
        await HealUntilAsync(setup, seed, Chain,
            () => ContainsAsync(setup.Devices[4], relayName),
            "multi-hop 0 -> 1 -> 2 -> 3 -> 4 propagation");
        Trace(seed, "Device 4 received the original device's change without a direct transport link");

        // Interrupted transport must leave work queued for retry, even in a multi-device mesh.
        var retryName = $"Stress {seed} failed transfer";
        await AddAsync(setup.Devices[4], retryName, $"retry-secret-{seed}-!", "interrupted transfer");
        expectedIndependentEntries.Add(retryName);
        var deltaCalls = setup.Devices[4].Host.Transport.DeltaSendCalls;
        setup.Devices[4].Host.Transport.FailNextDeltaSend = true;
        await KickAsync(setup.Devices[4], setup.Devices[3]);
        Assert.IsTrue(setup.Devices[4].Host.Transport.DeltaSendCalls > deltaCalls,
            $"Seed {seed}: transport failure injection never reached a delta send.");
        Assert.IsTrue(await setup.Devices[4].Host.HasPendingForAsync(setup.Devices[3].Host.Identity.LocalDeviceId),
            $"Seed {seed}: failed transfer unexpectedly discarded its durable queue.");
        await HealUntilAsync(setup, seed, Chain,
            () => ContainsAsync(setup.Devices[0], retryName),
            "failed transfer retry and indirect reverse propagation");

        // Restart a relay with its actual on-disk database intact while every network link is
        // down. Verify that the login survives, create another offline edit, and heal again.
        setup.SetLinks([]);
        await setup.RestartAsync(2);
        var afterRestartName = $"Stress {seed} restarted relay";
        await AddAsync(setup.Devices[2], afterRestartName, $"restarted-relay-{seed}-!", "written after restart");
        expectedIndependentEntries.Add(afterRestartName);
        Trace(seed, "Device 2 restarted and wrote a new entry while entirely disconnected");

        setup.SetLinks(Chain);
        await HealUntilAsync(setup, seed, Chain,
            () => EveryReplicaHasAsync(setup, expectedIndependentEntries),
            "eventual five-device convergence through adjacent links only");

        // Give every pair a chance to exchange anti-entropy inventories and clear outstanding
        // durable queues, then verify a stable logical result across all five replicas.
        setup.SetLinks(Mesh);
        await HealUntilAsync(setup, seed, Mesh,
            async () => await EveryReplicaHasAsync(setup, expectedIndependentEntries) &&
                        await AllQueuesIdleAsync(setup) &&
                        await EquivalentLogicalStatesAsync(setup),
            "full-mesh quiescence and byte-for-byte logical agreement");

        var snapshots = new List<LogicalState>();
        foreach (var device in setup.Devices)
            snapshots.Add(await CaptureLogicalStateAsync(device));
        for (var index = 1; index < snapshots.Count; index++)
            CollectionAssert.AreEqual(snapshots[0].Lines, snapshots[index].Lines,
                $"Seed {seed}: replica 0 and replica {index} disagree after quiescence.");

        foreach (var name in expectedIndependentEntries)
        {
            Assert.AreEqual(DeviceCount, snapshots.Count(snapshot => snapshot.PasswordNames.Contains(name)),
                $"Seed {seed}: independently created entry '{name}' was lost.");
        }
        Assert.IsTrue(snapshots[0].Lines.Any(line => line.Contains($"conflict-left-{seed}", StringComparison.Ordinal) ||
                                                           line.Contains($"conflict-right-{seed}", StringComparison.Ordinal)),
            $"Seed {seed}: neither concurrent edit to the shared credential survived.");
        Assert.AreEqual(DeviceCount, snapshots[0].TagNames.Count(name => name.StartsWith($"Stress tag {seed} ", StringComparison.Ordinal)));
        Assert.AreEqual(DeviceCount, snapshots[0].ColorNames.Count(name => name.StartsWith($"Stress color {seed} ", StringComparison.Ordinal)));

        // The conflict's resolution is allowed to choose either update or deletion, but every
        // replica must agree on the outcome. Unrelated password changes must all be preserved.
        Trace(seed, $"PASS: {expectedIndependentEntries.Count} independent password changes, " +
                    $"{snapshots[0].PasswordNames.Count} total passwords, " +
                    $"delete/edit item present={snapshots[0].PasswordIds.Contains(deletionId)}");
    }

}
