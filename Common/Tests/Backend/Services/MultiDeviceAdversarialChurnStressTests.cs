using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Requests;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

/// <summary>
/// A deliberately longer, second-generation stress scenario. It covers a peer that has
/// missed many subsequent publications and a deletion, loss of the durable outgoing
/// queue despite a retained encrypted snapshot, multiple rounds of conflicting writes,
/// restarts during partitions, and convergence again after previously reaching quiescence.
/// The real production services and authenticated in-process protocol are used through
/// the same fixture as MultiDeviceConvergenceStressTests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MultiDeviceAdversarialChurnStressTests : MultiDeviceSyncStressFixture
{
    private const int ExtendedWaveCount = 12;

    private static readonly (int A, int B)[] FourDeviceMesh =
    [
        (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)
    ];

    private static readonly (int A, int B)[] AlternateRelayChain =
    [
        (0, 2), (2, 4), (4, 1), (1, 3)
    ];

    // In every schedule Device 4 stays disconnected; several schedules also keep
    // the remaining four devices in distinct components or fully isolated.
    private static readonly (int A, int B)[][] ChurnTopologies =
    [
        [(0, 1), (2, 3)],
        [(0, 2), (1, 3)],
        [(0, 3)],
        [(1, 2)],
        [(0, 1), (1, 2)],
        [(0, 3), (1, 2)],
        []
    ];

    [TestMethod]
    [DataRow(271828)]
    [DataRow(314159)]
    [Timeout(1_200_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    [TestCategory("SyncStressExtended")]
    public async Task FiveDevices_StaleReplicaQueueLossAndRepeatedChurn_ConvergeWithoutResurrection(int seed)
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run extended multi-device synchronization stress tests.");

        var random = new Random(seed);
        var expectedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var activeNames = new HashSet<string>(StringComparer.Ordinal);
        var candidateSharedSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "original-shared-secret!"
        };
        Trace(seed, "EXTENDED: creating independent databases and authenticated five-device membership");
        await using var setup = await CreateFiveDeviceSetupAsync(seed);

        setup.SetLinks(Chain);
        await HealUntilAsync(setup, seed, Chain, async () =>
        {
            foreach (var device in setup.Devices)
            {
                var known = await device.Host.Endpoints.GetUserDevicesAsync(device.Token);
                if (known.Select(member => member.DeviceId).Distinct().Count() != DeviceCount)
                    return false;
            }
            return true;
        }, "extended preflight: all five signed memberships are known");

        var initial = await ReadAsync(setup.Devices[0]);
        var sharedId = initial.Passwords.Single(password => password.Name == "Stress shared credential").Id;
        var tombstoneTargetId = initial.Passwords.Single(password => password.Name == "Stress edit/delete credential").Id;
        foreach (var device in setup.Devices)
        {
            var current = await ReadAsync(device);
            Assert.IsTrue(current.Passwords.Any(password => password.Id == sharedId),
                $"Seed {seed}: enrolled replica is missing the shared baseline password.");
            Assert.IsTrue(current.Passwords.Any(password => password.Id == tombstoneTargetId),
                $"Seed {seed}: enrolled replica is missing the deletion baseline password.");
        }

        // Leave device 4 behind for the entire first epoch. Its snapshots must still
        // contain the item that the other four devices are about to delete.
        var mostRecentStaleWrite = string.Empty;
        for (var wave = 0; wave < ExtendedWaveCount; wave++)
        {
            var links = ChurnTopologies[random.Next(ChurnTopologies.Length)];
            setup.SetLinks(links);
            var writers = Shuffle(Enumerable.Range(0, 4), random);
            Trace(seed, $"EXTENDED wave={wave:D2}: edges={FormatLinks(links)}, writers={string.Join(",", writers)}; device 4 offline");

            // Every write here has an independent ID and an exact expected secret.
            // Unlike conflict winners, none of these additions may be lost or modified.
            foreach (var index in writers.Take(3))
            {
                var name = $"Extended {seed} active wave {wave:D2} device {index}";
                var secret = $"extended-secret-{seed}-{wave}-{index}-!";
                await AddAsync(setup.Devices[index], name, secret, $"wave={wave}; author={index}");
                expectedSecrets.Add(name, secret);
                activeNames.Add(name);
            }

            // Two writers edit the same logical item before their next sync attempt.
            // We deliberately do not prescribe a winner: convergence is the invariant.
            if (wave % 2 == 0)
            {
                foreach (var index in writers.Take(2))
                {
                    var secret = $"conflicting-secret-{seed}-{wave}-{index}-!";
                    await setup.Devices[index].Host.Endpoints.UpdatePasswordAsync(
                        setup.Devices[index].Token,
                        new UpdatePasswordRequest
                        {
                            Id = sharedId,
                            Description = $"extended conflicting edit wave={wave} device={index}",
                            Password = Encoding.UTF8.GetBytes(secret)
                        });
                    candidateSharedSecrets.Add(secret);
                }
            }

            if (wave == 2)
            {
                // This item is NEVER edited again. Device 4 retains an older copy;
                // its later publications must not resurrect the deleted item.
                await setup.Devices[0].Host.Endpoints.RemovePasswordsAsync(
                    setup.Devices[0].Token, [tombstoneTargetId]);
                Trace(seed, "EXTENDED: device 0 deleted the baseline item while device 4 was offline");
            }

            if (wave % 3 == 0)
            {
                var name = $"Extended {seed} stale device 4 wave {wave:D2}";
                var secret = $"stale-device-secret-{seed}-{wave}-!";
                await AddAsync(setup.Devices[4], name, secret, "published while disconnected from every peer");
                expectedSecrets.Add(name, secret);
                mostRecentStaleWrite = name;
            }

            if (wave is 5 or 10)
            {
                // Restart a different relay in the middle of the rolling partition
                // schedule, preserving its on-disk SQLite state and sync identity.
                var index = wave == 5 ? 1 : 2;
                await setup.RestartAsync(index);
                setup.SetLinks(links);
                Trace(seed, $"EXTENDED wave={wave:D2}: restarted relay {index} while partitioned");
            }

            await ExerciseAvailableEdgesAsync(setup, links, random);
        }

        // The four reachable devices must agree about the deletion and every
        // independent active-device write, while device 4 remains entirely offline.
        setup.SetLinks(FourDeviceMesh);
        int[] active = [0, 1, 2, 3];
        await HealUntilAsync(setup, seed, FourDeviceMesh, async () =>
            await SubsetHasNamesAsync(setup, active, activeNames) &&
            await SubsetLacksPasswordAsync(setup, active, tombstoneTargetId) &&
            await EquivalentSubsetAsync(setup, active),
            "extended: active quorum converges with deletion while fifth replica remains stale");

        Assert.IsTrue((await ReadAsync(setup.Devices[4])).Passwords
                .Any(password => password.Id == tombstoneTargetId),
            $"Seed {seed}: the isolated device no longer has the old item; the stale-replay precondition was not met.");

        // Publish yet another generation from the stale replica. Deleting *all*
        // its outgoing queue entries simulates loss of queue metadata, not loss of
        // the durable encrypted snapshot. Anti-entropy must recover the publication.
        setup.SetLinks([]);
        var lateStaleSecret = $"stale-late-secret-{seed}-!";
        mostRecentStaleWrite = $"Extended {seed} stale final publication";
        await AddAsync(setup.Devices[4], mostRecentStaleWrite, lateStaleSecret,
            "latest publication whose outgoing queues will be erased");
        expectedSecrets.Add(mostRecentStaleWrite, lateStaleSecret);
        await setup.Devices[4].Host.PublishCurrentUserSnapshotAsync();

        var removedQueueEntries = 0;
        for (var index = 0; index < 4; index++)
        {
            var peerId = setup.Devices[index].Host.Identity.LocalDeviceId;
            removedQueueEntries += await setup.Devices[4].Host.DeletePendingForAsync(peerId);
            Assert.IsFalse(await setup.Devices[4].Host.HasPendingForAsync(peerId),
                $"Seed {seed}: failed to remove the outgoing queue for peer {index}.");
        }
        Assert.IsTrue(removedQueueEntries > 0,
            $"Seed {seed}: queue loss was not exercised; no pending outgoing entries existed.");
        Trace(seed, $"EXTENDED: removed {removedQueueEntries} pending queue entries on device 4");

        // Lose the process and then reconnect using only one direct link. The
        // received publication must come from anti-entropy, not a surviving queue.
        await setup.RestartAsync(4);
        Assert.IsTrue(await ContainsAsync(setup.Devices[4], mostRecentStaleWrite),
            $"Seed {seed}: device 4 lost its durable local write during restart.");
        setup.SetLinks([(4, 0)]);
        await HealUntilAsync(setup, seed, [(4, 0)], async () =>
            await ContainsAsync(setup.Devices[0], mostRecentStaleWrite) &&
            await SubsetLacksPasswordAsync(setup, [4], tombstoneTargetId),
            "extended: restart plus missing queues recovers newest snapshot and deletion via anti-entropy");

        var allNames = expectedSecrets.Keys.ToHashSet(StringComparer.Ordinal);
        setup.SetLinks(Mesh);
        await HealUntilAsync(setup, seed, Mesh, async () =>
            await EveryReplicaHasAsync(setup, allNames) &&
            await SubsetLacksPasswordAsync(setup, [0, 1, 2, 3, 4], tombstoneTargetId) &&
            await AllQueuesIdleAsync(setup) &&
            await EquivalentLogicalStatesAsync(setup),
            "extended: first fully quiescent convergence after queue loss and stale replay");
        await AssertIndependentSecretsAsync(setup, seed, expectedSecrets);
        Trace(seed, "EXTENDED: first convergence achieved; running a new epoch from all five replicas");

        // A second wave after previous convergence tests that new publications do
        // not get suppressed by old high-water/revision knowledge. All five writers
        // are again offline at the time of the edits.
        setup.SetLinks([]);
        foreach (var index in Shuffle(Enumerable.Range(0, DeviceCount), random))
        {
            var name = $"Extended {seed} second epoch device {index}";
            var secret = $"second-epoch-secret-{seed}-{index}-!";
            await AddAsync(setup.Devices[index], name, secret, "written after prior full quiescence");
            expectedSecrets.Add(name, secret);

            var conflictingSecret = $"second-epoch-shared-{seed}-{index}-!";
            await setup.Devices[index].Host.Endpoints.UpdatePasswordAsync(
                setup.Devices[index].Token,
                new UpdatePasswordRequest
                {
                    Id = sharedId,
                    Description = $"second epoch competing edit from device {index}",
                    Password = Encoding.UTF8.GetBytes(conflictingSecret)
                });
            candidateSharedSecrets.Add(conflictingSecret);
        }

        await setup.RestartAsync(2);
        var restartedName = $"Extended {seed} second epoch restarted relay";
        var restartedSecret = $"second-epoch-restart-secret-{seed}-!";
        await AddAsync(setup.Devices[2], restartedName, restartedSecret,
            "created offline after restart in second epoch");
        expectedSecrets.Add(restartedName, restartedSecret);
        allNames = expectedSecrets.Keys.ToHashSet(StringComparer.Ordinal);

        // Rejoin along a completely different relay chain, with no direct 0--3
        // edge, then finish in a mesh so even non-neighbor durable queues can drain.
        setup.SetLinks(AlternateRelayChain);
        await HealUntilAsync(setup, seed, AlternateRelayChain, async () =>
            await EveryReplicaHasAsync(setup, allNames) &&
            await SubsetLacksPasswordAsync(setup, [0, 1, 2, 3, 4], tombstoneTargetId) &&
            await EquivalentLogicalStatesAsync(setup),
            "extended: second epoch converges through a reordered multi-hop chain");

        setup.SetLinks(Mesh);
        await HealUntilAsync(setup, seed, Mesh, async () =>
            await EveryReplicaHasAsync(setup, allNames) &&
            await SubsetLacksPasswordAsync(setup, [0, 1, 2, 3, 4], tombstoneTargetId) &&
            await AllQueuesIdleAsync(setup) &&
            await EquivalentLogicalStatesAsync(setup),
            "extended: second full-mesh quiescence after another partition and restart");

        await AssertIndependentSecretsAsync(setup, seed, expectedSecrets);
        var shared = (await ReadAsync(setup.Devices[0])).Passwords.Single(p => p.Id == sharedId);
        var winnerBytes = await setup.Devices[0].Host.Endpoints.GetUnsecurePasswordAsync(
            setup.Devices[0].Token, shared.Id);
        Assert.IsTrue(candidateSharedSecrets.Contains(Encoding.UTF8.GetString(winnerBytes)),
            $"Seed {seed}: the shared password does not contain any submitted competing value.");

        // Re-exchange inventories without additional writes. No late duplicate or
        // stale publication may change the already-converged logical result.
        var stableLines = (await CaptureLogicalStateAsync(setup.Devices[0])).Lines;
        foreach (var edge in Mesh)
        {
            await KickAsync(setup.Devices[edge.A], setup.Devices[edge.B]);
            await KickAsync(setup.Devices[edge.B], setup.Devices[edge.A]);
        }
        foreach (var device in setup.Devices)
        {
            var candidateLines = (await CaptureLogicalStateAsync(device)).Lines;
            CollectionAssert.AreEqual(stableLines, candidateLines,
                $"Seed {seed}: a supposedly stable replica changed after redundant anti-entropy exchanges.");
        }
        Trace(seed, $"EXTENDED PASS: {expectedSecrets.Count} individually verified secrets, " +
            "two quiescence epochs, stale tombstone, wiped outgoing queues, multiple restarts");
    }

    private static async Task ExerciseAvailableEdgesAsync(
        FiveDeviceSetup setup, (int A, int B)[] edges, Random random)
    {
        var shuffled = Shuffle(edges, random);
        if (shuffled.Length == 0)
            return;

        // Disjoint peer pairs can synchronize truly concurrently without sharing a
        // test host. Other topologies use alternating one-way sessions to avoid
        // making a particular host's task scheduler the source of nondeterminism.
        var disjoint = shuffled.SelectMany(edge => new[] { edge.A, edge.B })
            .Distinct().Count() == shuffled.Length * 2;
        if (disjoint)
        {
            var sessions = shuffled.Select(edge => random.Next(2) == 0
                ? KickAsync(setup.Devices[edge.A], setup.Devices[edge.B])
                : KickAsync(setup.Devices[edge.B], setup.Devices[edge.A])).ToArray();
            await Task.WhenAll(sessions);
        }
        else
        {
            foreach (var edge in shuffled)
            {
                if (random.Next(2) == 0)
                    await KickAsync(setup.Devices[edge.A], setup.Devices[edge.B]);
                else
                    await KickAsync(setup.Devices[edge.B], setup.Devices[edge.A]);
            }
        }
    }

    private static async Task<bool> SubsetHasNamesAsync(
        FiveDeviceSetup setup, IEnumerable<int> indices, IReadOnlySet<string> expected)
    {
        foreach (var index in indices)
        {
            var names = (await ReadAsync(setup.Devices[index])).Passwords
                .Select(password => password.Name).ToHashSet(StringComparer.Ordinal);
            if (!expected.All(names.Contains))
                return false;
        }
        return true;
    }

    private static async Task<bool> SubsetLacksPasswordAsync(
        FiveDeviceSetup setup, IEnumerable<int> indices, Guid passwordId)
    {
        foreach (var index in indices)
        {
            if ((await ReadAsync(setup.Devices[index])).Passwords.Any(password => password.Id == passwordId))
                return false;
        }
        return true;
    }

    private static async Task<bool> EquivalentSubsetAsync(
        FiveDeviceSetup setup, IReadOnlyList<int> indices)
    {
        var baseline = (await CaptureLogicalStateAsync(setup.Devices[indices[0]])).Lines;
        foreach (var index in indices.Skip(1))
        {
            var candidate = (await CaptureLogicalStateAsync(setup.Devices[index])).Lines;
            if (!baseline.SequenceEqual(candidate, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    private static async Task AssertIndependentSecretsAsync(
        FiveDeviceSetup setup, int seed, IReadOnlyDictionary<string, string> expected)
    {
        for (var index = 0; index < setup.Devices.Count; index++)
        {
            var device = setup.Devices[index];
            var data = await ReadAsync(device);
            var byName = data.Passwords.ToLookup(password => password.Name, StringComparer.Ordinal);
            foreach (var (name, secret) in expected)
            {
                var matches = byName[name].ToArray();
                Assert.AreEqual(1, matches.Length,
                    $"Seed {seed}: device {index} lost or duplicated independent password '{name}'.");
                var recoveredSecret = await device.Host.Endpoints.GetUnsecurePasswordAsync(
                    device.Token, matches[0].Id);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(secret), recoveredSecret,
                    $"Seed {seed}: device {index} changed the secret of independent password '{name}'.");
            }
        }
    }
}
