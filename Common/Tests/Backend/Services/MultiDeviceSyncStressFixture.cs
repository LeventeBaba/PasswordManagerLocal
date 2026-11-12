using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

/// <summary>
/// Production-backed five-device synchronization test infrastructure shared by the
/// convergence and extended adversarial-churn test classes.
/// </summary>
public abstract class MultiDeviceSyncStressFixture
{
    protected const int DeviceCount = 5;
    protected const int MaximumHealingPasses = 35;
    protected const int TestTimeoutMilliseconds = 720_000;
    protected static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(25);

    protected static readonly (int A, int B)[] Chain = [(0, 1), (1, 2), (2, 3), (3, 4)];
    protected static readonly (int A, int B)[] Mesh =
    [
        (0, 1), (0, 2), (0, 3), (0, 4),
        (1, 2), (1, 3), (1, 4),
        (2, 3), (2, 4), (3, 4)
    ];

    public TestContext TestContext { get; set; } = null!;

    protected async Task<FiveDeviceSetup> CreateFiveDeviceSetupAsync(int seed)
    {
        var setup = new FiveDeviceSetup($"syncstress{seed}{Guid.NewGuid():N}"[..24]);
        try
        {
            for (var index = 0; index < DeviceCount; index++)
                setup.Devices.Add(new TestDevice(await ProductionSyncTestHost.CreateAsync()));

            var original = setup.Devices[0];
            original.Token = await original.Host.Endpoints.RegisterAsync(
                original.Host.CreateRegistrationRequest(setup.Username));
            await AddAsync(original, "Stress shared credential", "original-shared-secret!", "initial common value");
            await AddAsync(original, "Stress edit/delete credential", "original-delete-secret!", "initial common value");
            await original.Host.EnableSyncAsync(original.Token);

            for (var index = 1; index < DeviceCount; index++)
            {
                var parent = setup.Devices[index - 1];
                var next = setup.Devices[index];
                // Enrollment itself is routed through the actual protocol; no database cloning.
                setup.SetLinks([(index - 1, index)]);
                var code = await next.Host.Endpoints.StartDeviceEnrollmentAsync();
                await parent.Host.Endpoints.AddDeviceByCodeAsync(parent.Token, code.Code);
                next.Token = await next.Host.Endpoints.LoginAsync(
                    next.Host.CreateLoginRequest(setup.Username));
                // Unlike the terminal enrollment target in the three-device fixture, this
                // device will become the *enrolling parent* in the next iteration. Explicitly
                // enable its local synchronization setting, just as the existing integration
                // test does for intermediate devices.
                await next.Host.EnableSyncAsync(next.Token);
                Trace(seed, $"Enrolled device {index} using device {index - 1}");

                // Complete the enrollment pair's initial authenticated synchronization before
                // moving the enrollment chain forward. A newly enrolled parent can otherwise
                // carry still-pending control and snapshot work into the next enrollment. That
                // tests overlapping enrollment transactions, not the partitioned multi-writer
                // data-convergence scenario this test is intended to exercise.
                var parentControlBefore = parent.Host.Transport.ControlOperationInventoryExchangeCalls;
                var nextControlBefore = next.Host.Transport.ControlOperationInventoryExchangeCalls;
                var expectedMemberships = setup.Devices.Take(index + 1)
                    .Select(device => device.Host.Identity.LocalDeviceId).ToHashSet();
                await HealUntilAsync(setup, seed, [(index - 1, index)], async () =>
                {
                    var parentMembers = (await parent.Host.Endpoints.GetUserDevicesAsync(parent.Token))
                        .Select(member => member.DeviceId).ToHashSet();
                    var nextMembers = (await next.Host.Endpoints.GetUserDevicesAsync(next.Token))
                        .Select(member => member.DeviceId).ToHashSet();
                    return parentMembers.SetEquals(expectedMemberships) &&
                           nextMembers.SetEquals(expectedMemberships) &&
                           parent.Host.Transport.ControlOperationInventoryExchangeCalls > parentControlBefore &&
                           next.Host.Transport.ControlOperationInventoryExchangeCalls > nextControlBefore &&
                           !await parent.Host.HasPendingForAsync(next.Host.Identity.LocalDeviceId) &&
                           !await next.Host.HasPendingForAsync(parent.Host.Identity.LocalDeviceId);
                }, $"post-enrollment synchronization of devices {index - 1} and {index}");
            }
            return setup;
        }
        catch
        {
            await setup.DisposeAsync();
            throw;
        }
    }

    protected async Task HealUntilAsync(
        FiveDeviceSetup setup, int seed, (int A, int B)[] links,
        Func<Task<bool>> converged, string description)
    {
        for (var pass = 0; pass < MaximumHealingPasses; pass++)
        {
            if (await converged())
            {
                Trace(seed, $"{description}: success after {pass} passes");
                return;
            }

            // Both directions matter. Reverse the edge order every other round to ensure
            // that forwarding is not accidentally dependent on an always-favorable schedule.
            var current = pass % 2 == 0 ? links : links.Reverse().ToArray();
            foreach (var (a, b) in current)
            {
                if (pass % 2 == 0)
                {
                    await KickAsync(setup.Devices[a], setup.Devices[b]);
                    await KickAsync(setup.Devices[b], setup.Devices[a]);
                }
                else
                {
                    await KickAsync(setup.Devices[b], setup.Devices[a]);
                    await KickAsync(setup.Devices[a], setup.Devices[b]);
                }
            }
        }

        var diagnostics = new StringBuilder();
        foreach (var device in setup.Devices)
        {
            var data = await ReadAsync(device);
            var pending = new List<string>();
            foreach (var peer in setup.Devices.Where(peer => !ReferenceEquals(peer, device)))
            {
                if (await device.Host.HasPendingForAsync(peer.Host.Identity.LocalDeviceId))
                    pending.Add(peer.Host.Identity.LocalDeviceId.ToString("N"));
            }
            var passwordNames = string.Join(",", data.Passwords.Select(password => password.Name)
                .Order(StringComparer.Ordinal));
            // Explain *why* a preflight membership check failed, rather than reporting
            // only queue counts. The control-plane state and transport failures distinguish
            // missing delivery, rejected operations, and peers that cannot start sessions.
            var visibleMembers = await device.Host.Endpoints.GetUserDevicesAsync(device.Token);
            using var scope = device.Host.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var userIds = await users.ListUserIdsAsync();
            var controlDetails = new List<string>();
            foreach (var userId in userIds)
            {
                var state = await scope.ServiceProvider.GetRequiredService<IUserControlStateRepository>()
                    .GetAsync(userId);
                var operations = await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                    .ListForUserAsync(userId);
                var faults = await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>()
                    .ListActiveForUserAsync(userId);
                var localSync = await scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>()
                    .IsSyncOnAsync(userId);
                controlDetails.Add($"User={userId:N}/LocalSync={localSync}/" +
                    $"MembershipEpoch={state?.AppliedMembershipEpoch}/Conflict={state?.HasConflict}/" +
                    $"Operations=[{string.Join(",", operations.Select(op =>
                        $"{op.OperationType}:{op.PreviousMembershipEpoch}->{op.ResultingMembershipEpoch}:{op.Status}:{op.StatusReason}"))}]/" +
                    $"Faults=[{string.Join(",", faults.Select(fault =>
                        $"{fault.Kind}:{fault.Status}:{fault.DiagnosticCode}"))}]");
            }
            var transportFailures = setup.Devices
                .Where(peer => !ReferenceEquals(peer, device))
                .Select(peer => (Id: peer.Host.Identity.LocalDeviceId,
                                 Reason: device.Host.Transport.GetLastDeltaSendFailure(peer.Host.Identity.FingerprintHex)))
                .Where(failure => failure.Reason is not null)
                .Select(failure => $"{failure.Id:N}:{failure.Reason}");
            diagnostics.AppendLine($"  Device={device.Host.Identity.LocalDeviceId:N}, " +
                                   $"IdentitySync={device.Host.Identity.IsSyncOn}, " +
                                   $"Members=[{string.Join(",", visibleMembers.Select(member => member.DeviceId.ToString("N")))}], " +
                                   $"Passwords={data.Passwords.Count}, Tags={data.Tags.Count}, " +
                                   $"Colors={data.CustomColors.Count}, " +
                                   $"PendingPeers=[{string.Join(",", pending)}], " +
                                   $"DeltaSends={device.Host.Transport.DeltaSendCalls}, " +
                                   $"SnapshotInventories={device.Host.Transport.SnapshotInventoryExchangeCalls}, " +
                                   $"ControlInventories={device.Host.Transport.ControlOperationInventoryExchangeCalls}, " +
                                   $"TransportFailures=[{string.Join(",", transportFailures)}], " +
                                   $"PasswordNames=[{passwordNames}]");
            diagnostics.AppendLine($"    Control={string.Join("; " , controlDetails)}");
        }
        Assert.Fail($"Seed {seed}: {description} failed after {MaximumHealingPasses} passes. " +
                    $"Reachability={FormatLinks(links)}\n{diagnostics}");
    }

    protected static async Task KickAsync(TestDevice source, TestDevice target)
    {
        using var deadline = new CancellationTokenSource(SessionTimeout);
        await source.Host.StartSyncToAsync(target.Host, deadline.Token);
        await source.Host.WaitForSyncIdleAsync(target.Host.Identity.LocalDeviceId, deadline.Token);
    }

    protected static Task<SavedPasswordsResponse> ReadAsync(TestDevice device) =>
        device.Host.Endpoints.GetSavedPasswordsAsync(device.Token);

    protected static async Task<bool> ContainsAsync(TestDevice device, string name) =>
        (await ReadAsync(device)).Passwords.Any(password => password.Name == name);

    protected static async Task<bool> EveryReplicaHasAsync(FiveDeviceSetup setup, IReadOnlySet<string> names)
    {
        foreach (var device in setup.Devices)
        {
            var existing = (await ReadAsync(device)).Passwords.Select(password => password.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.All(existing.Contains))
                return false;
        }
        return true;
    }

    protected static async Task<bool> AllQueuesIdleAsync(FiveDeviceSetup setup)
    {
        foreach (var source in setup.Devices)
        foreach (var target in setup.Devices)
        {
            if (!ReferenceEquals(source, target) &&
                await source.Host.HasPendingForAsync(target.Host.Identity.LocalDeviceId))
                return false;
        }
        return true;
    }

    protected static async Task<bool> EquivalentLogicalStatesAsync(FiveDeviceSetup setup)
    {
        var baseline = (await CaptureLogicalStateAsync(setup.Devices[0])).Lines;
        foreach (var device in setup.Devices.Skip(1))
        {
            // Complete the asynchronous snapshot capture before comparing the arrays.
            // Otherwise overload resolution may create a ReadOnlySpan<string> that crosses await.
            var candidate = (await CaptureLogicalStateAsync(device)).Lines;
            if (!baseline.SequenceEqual(candidate, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    protected static async Task<LogicalState> CaptureLogicalStateAsync(TestDevice device)
    {
        var response = await ReadAsync(device);
        var lines = new List<string>();
        foreach (var password in response.Passwords.OrderBy(password => password.Id))
        {
            var secret = await device.Host.Endpoints.GetUnsecurePasswordAsync(device.Token, password.Id);
            lines.Add($"password:{password.Id:N}:{password.Name}:{password.Description}:{password.Color}:" +
                      $"{string.Join(",", password.TagIds.Order())}:{Convert.ToHexString(SHA256.HashData(secret))}");
        }
        foreach (var tag in response.Tags.OrderBy(tag => tag.Id))
            lines.Add($"tag:{tag.Id:N}:{tag.Name}:{tag.Color}");
        foreach (var color in response.CustomColors.OrderBy(color => color.Id))
            lines.Add($"color:{color.Id:N}:{color.ColorName}:{color.ColorCode}");
        lines.Sort(StringComparer.Ordinal);
        return new LogicalState(
            lines.ToArray(),
            response.Passwords.Select(password => password.Id).ToHashSet(),
            response.Passwords.Select(password => password.Name).ToHashSet(StringComparer.Ordinal),
            response.Tags.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal),
            response.CustomColors.Select(color => color.ColorName ?? "").ToHashSet(StringComparer.Ordinal));
    }

    protected static Task AddAsync(TestDevice device, string name, string secret, string description) =>
        device.Host.Endpoints.AddNewPasswordAsync(device.Token, new NewPasswordRequest
        {
            Name = name,
            Description = description,
            Password = Encoding.UTF8.GetBytes(secret),
            Color = "#FF245678",
            TagIds = []
        });

    protected static T[] Shuffle<T>(IEnumerable<T> values, Random random)
    {
        var result = values.ToArray();
        for (var i = result.Length - 1; i > 0; i--)
        {
            var swap = random.Next(i + 1);
            (result[i], result[swap]) = (result[swap], result[i]);
        }
        return result;
    }

    protected static string FormatLinks(IEnumerable<(int A, int B)> links) =>
        string.Join(" ", links.Select(edge => $"{edge.A}<->{edge.B}"));

    protected void Trace(int seed, string message) => TestContext.WriteLine($"[SyncStress seed={seed}] {message}");

    protected sealed class TestDevice(ProductionSyncTestHost host)
    {
        public ProductionSyncTestHost Host { get; set; } = host;
        public Guid Token { get; set; }
    }

    protected sealed record LogicalState(
        string[] Lines,
        HashSet<Guid> PasswordIds,
        HashSet<string> PasswordNames,
        HashSet<string> TagNames,
        HashSet<string> ColorNames);

    protected sealed class FiveDeviceSetup(string username) : IAsyncDisposable
    {
        public string Username { get; } = username;
        public List<TestDevice> Devices { get; } = [];

        public void SetLinks(IEnumerable<(int A, int B)> links)
        {
            foreach (var device in Devices)
                device.Host.Transport.Disconnect();
            foreach (var (a, b) in links)
            {
                Devices[a].Host.ConnectTo(Devices[b].Host);
                Devices[b].Host.ConnectTo(Devices[a].Host);
            }
        }

        public async Task RestartAsync(int index)
        {
            var device = Devices[index];
            device.Host = await device.Host.RestartAsync();
            device.Token = await device.Host.Endpoints.LoginAsync(device.Host.CreateLoginRequest(Username));
            await device.Host.ActivateImportedSynchronizationAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var device in Devices)
                device.Host.Transport.Disconnect();
            foreach (var device in Devices.AsEnumerable().Reverse())
                await device.Host.DisposeAsync();
        }
    }
}
