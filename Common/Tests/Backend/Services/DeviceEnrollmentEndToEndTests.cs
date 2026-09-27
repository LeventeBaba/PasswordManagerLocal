using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class DeviceEnrollmentEndToEndTests
{
    private const int TestTimeoutMilliseconds = 60_000;
    private static readonly TimeSpan EnrollmentPhaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(2);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task FullEnrollmentBetweenTwoProductionBackends_TransfersProfileAndAllowsLogin()
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);

        Assert.IsInstanceOfType<PasswordManagerLocal.Common.Backend.Services.SyncVersionClockService>(
            source.Services.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.Services.ISyncVersionClockService>());

        var username = $"enrolle2e{Guid.NewGuid().ToString("N")[..16]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        var expectedSecret = Encoding.UTF8.GetBytes("enrollment-secret-value");

        await source.Endpoints.AddPasswordTagAsync(sourceToken, new NewPasswordTagRequest
        {
            Name = "Enrollment tag",
            Color = "#FF102030"
        });
        await source.Endpoints.AddCustomUserColorsAsync(sourceToken,
        [
            new NewCustomUserColorRequest
            {
                ColorName = "Enrollment color",
                ColorCode = "#FF405060"
            }
        ]);

        var sourceDataBeforeEnrollment = await source.Endpoints.GetSavedPasswordsAsync(sourceToken);
        Assert.AreEqual(1, sourceDataBeforeEnrollment.Tags.Count);
        Assert.AreEqual(1, sourceDataBeforeEnrollment.CustomColors.Count);
        var sourceTag = sourceDataBeforeEnrollment.Tags[0];
        var sourceColor = sourceDataBeforeEnrollment.CustomColors[0];

        await source.Endpoints.AddNewPasswordAsync(sourceToken, new NewPasswordRequest
        {
            Name = "Enrollment password",
            Description = "Transferred through the enrollment snapshot.",
            Color = sourceColor.ColorCode,
            Password = expectedSecret,
            TagIds = [sourceTag.Id]
        });
        await source.Endpoints.SetLocalUserSyncOnAsync(sourceToken, true);
        var enrollmentCode = await RunPhaseAsync(
            "target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));

        await RunPhaseAsync(
            "source authoritative enrollment, snapshot transfer, and target import",
            ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct));

        var targetStatus = await target.Endpoints.GetDeviceEnrollmentStatusAsync();
        Assert.AreEqual(DeviceEnrollmentState.Completed, targetStatus.State);
        Assert.AreEqual(1, (await GetUserIdsAsync(target)).Count);

        var sourceIdentity = await GetOnlyLoginIdentityAsync(source);
        var sourceUser = await GetOnlyUserAsync(source);
        Assert.AreEqual(sourceUser.MembershipEpoch, sourceIdentity.MembershipEpoch);
        Assert.AreEqual(UserLoginIdentityStatus.Active, sourceIdentity.Status);

        await source.Endpoints.LogoutAsync(sourceToken);
        var sourceReloginToken = await RunPhaseAsync(
            "original device login after authoritative enrollment membership transition",
            ct => source.Endpoints.LoginAsync(source.CreateLoginRequest(username), ct));
        Assert.AreNotEqual(Guid.Empty, sourceReloginToken);

        var targetIdentity = await GetOnlyLoginIdentityAsync(target);
        Assert.AreEqual(UserLoginIdentityStatus.Active, targetIdentity.Status);
        Assert.IsNotNull(
            await GetOnlyCanonicalCheckpointAsync(target),
            "Enrollment import must create the target installation's canonical checkpoint before first login.");

        var targetToken = await RunPhaseAsync(
            "first login on the newly enrolled backend",
            ct => target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));
        var profile = await target.Endpoints.GetUserProfileInfoAsync(targetToken);
        var devices = await target.Endpoints.GetUserDevicesAsync(targetToken);
        var targetData = await target.Endpoints.GetSavedPasswordsAsync(targetToken);
        var commit = await GetOnlyEnrollmentCommitAsync(source);

        Assert.AreEqual(username, profile.Username);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, commit.Status);
        Assert.IsNotNull(commit.CompletedAtUtc);
        Assert.AreEqual(target.Identity.LocalDeviceId, commit.TargetDeviceId);
        Assert.AreEqual(2, devices.Select(device => device.DeviceId).Distinct().Count());
        Assert.IsTrue(devices.Any(device =>
            device.DeviceId == source.Identity.LocalDeviceId && !device.IsCurrentDevice));
        Assert.IsTrue(devices.Any(device =>
            device.DeviceId == target.Identity.LocalDeviceId && device.IsCurrentDevice));

        Assert.AreEqual(1, targetData.Passwords.Count);
        Assert.AreEqual(1, targetData.Tags.Count);
        Assert.AreEqual(1, targetData.CustomColors.Count);
        Assert.AreEqual(sourceTag.Id, targetData.Tags[0].Id);
        Assert.AreEqual("Enrollment tag", targetData.Tags[0].Name);
        Assert.AreEqual(sourceColor.Id, targetData.CustomColors[0].Id);
        Assert.AreEqual("Enrollment color", targetData.CustomColors[0].ColorName);
        Assert.AreEqual("Enrollment password", targetData.Passwords[0].Name);
        Assert.AreEqual("Transferred through the enrollment snapshot.", targetData.Passwords[0].Description);
        Assert.AreEqual(sourceColor.ColorCode, targetData.Passwords[0].Color);
        CollectionAssert.AreEqual(
            new[] { sourceTag.Id },
            targetData.Passwords[0].TagIds.ToArray());
        CollectionAssert.AreEqual(
            expectedSecret,
            await target.Endpoints.GetUnsecurePasswordAsync(targetToken, targetData.Passwords[0].Id));

        Assert.AreEqual(1, source.Transport.EnrollmentInfoCalls);
        Assert.AreEqual(1, source.Transport.EnrollmentCompletionCalls);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task EmptyNonQuarantineDiagnostics_AreNormalizedDuringEnrollment()
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);

        var username = $"enrollempty{Guid.NewGuid().ToString("N")[..16]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.Endpoints.SetLocalUserSyncOnAsync(sourceToken, true);
        await SetEmptyDiagnosticsOnHealthySnapshotAsync(source);

        var enrollmentCode = await RunPhaseAsync(
            "target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        await RunPhaseAsync(
            "source authoritative enrollment, snapshot transfer, and target import",
            ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct));

        var targetToken = await RunPhaseAsync(
            "first login on the newly enrolled backend",
            ct => target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));
        Assert.AreNotEqual(Guid.Empty, targetToken);

        using var scope = target.Services.CreateScope();
        var userId = (await scope.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .ListUserIdsAsync()).Single();
        var importedSnapshots = await scope.ServiceProvider
            .GetRequiredService<IUserSyncSnapshotRepository>()
            .ListForUserAsync(userId);

        Assert.IsTrue(importedSnapshots.Count > 0);
        Assert.IsTrue(importedSnapshots
            .Where(row => row.Status != UserSyncSnapshotStatus.IsolatedFork)
            .All(row => row.QuarantineReason is null && row.ConflictingSnapshotHash is null));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task LostSuccessReply_RetryWithNewCode_ReusesCommitAndImportsIdempotently()
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);

        var username = $"enrollretry{Guid.NewGuid().ToString("N")[..16]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.Endpoints.SetLocalUserSyncOnAsync(sourceToken, true);
        var firstCode = await RunPhaseAsync(
            "first target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        source.Transport.ThrowAfterNextSuccessfulEnrollmentCompletion = true;

        var firstFailure = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            RunPhaseAsync(
                "first enrollment transfer with simulated lost success reply",
                ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, firstCode.Code, ct)));

        Assert.AreEqual(DeviceEnrollmentErrorCode.NewDeviceConnectionFailed, firstFailure.ErrorCode);
        Assert.IsTrue(firstFailure.RecoveryAvailable);
        Assert.IsTrue(firstFailure.TransferPending);
        Assert.AreEqual(DeviceEnrollmentState.Completed,
            (await target.Endpoints.GetDeviceEnrollmentStatusAsync()).State);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed,
            (await GetOnlyEnrollmentCommitAsync(source)).Status);

        var retryCode = await RunPhaseAsync(
            "retry target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        await RunPhaseAsync(
            "retry enrollment transfer and idempotent target import",
            ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, retryCode.Code, ct));

        var commits = await GetEnrollmentCommitsAsync(source);
        var targetUserIds = await GetUserIdsAsync(target);
        var targetToken = await RunPhaseAsync(
            "first login on the newly enrolled backend",
            ct => target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));
        var devices = await target.Endpoints.GetUserDevicesAsync(targetToken);

        Assert.AreEqual(1, commits.Count);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, commits[0].Status);
        Assert.AreEqual(1, targetUserIds.Count);
        Assert.AreEqual(2, devices.Select(device => device.DeviceId).Distinct().Count());
        Assert.AreEqual(2, source.Transport.EnrollmentCompletionCalls);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task TamperedEncryptedSnapshot_IsRejectedWithoutCreatingTargetProfile()
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);

        var username = $"enrolltamper{Guid.NewGuid().ToString("N")[..16]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.Endpoints.SetLocalUserSyncOnAsync(sourceToken, true);
        var enrollmentCode = await RunPhaseAsync(
            "target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        source.Transport.TamperNextEnrollmentSnapshot = true;

        var failure = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            RunPhaseAsync(
                "tampered snapshot rejection and recoverable commit recording",
                ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct)));

        var targetUserIds = await GetUserIdsAsync(target);
        var targetStatus = await target.Endpoints.GetDeviceEnrollmentStatusAsync();
        var commit = await GetOnlyEnrollmentCommitAsync(source);

        Assert.AreEqual(DeviceEnrollmentErrorCode.ProfileDataInvalid, failure.ErrorCode);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed, commit.Status);
        Assert.AreEqual(0, targetUserIds.Count);
        Assert.AreEqual(DeviceEnrollmentState.Waiting, targetStatus.State);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task MismatchedClientCertificateFingerprint_IsRejectedWithoutImportingProfile()
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);

        var username = $"enrollcert{Guid.NewGuid().ToString("N")[..16]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.Endpoints.SetLocalUserSyncOnAsync(sourceToken, true);
        var enrollmentCode = await RunPhaseAsync(
            "target enrollment listener startup and authenticated self-test",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        source.Transport.ClientCertificateFingerprintOverride = new string('F', 64);

        var failure = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            RunPhaseAsync(
                "client-certificate mismatch rejection and recoverable commit recording",
                ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct)));

        Assert.AreEqual(DeviceEnrollmentErrorCode.NewDeviceRejected, failure.ErrorCode);
        Assert.AreEqual(0, (await GetUserIdsAsync(target)).Count);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed,
            (await GetOnlyEnrollmentCommitAsync(source)).Status);
    }

    private async Task<T> RunPhaseAsync<T>(
        string phase,
        Func<CancellationToken, Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operation);

        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {phase}");
        using var cancellation = new CancellationTokenSource();

        // Execute the endpoint call on a worker so a synchronous SQLite wait cannot block the
        // MSTest control thread before the watchdog is installed. The cancellation token is passed
        // into the real backend operation; unlike Task.WaitAsync(timeout), timing out here also asks
        // the underlying operation to unwind and release its enrollment/lifecycle/DbContext leases.
        var operationTask = Task.Run(() => operation(cancellation.Token));
        var watchdog = Task.Delay(EnrollmentPhaseTimeout);
        var completed = await Task.WhenAny(operationTask, watchdog);
        if (completed == operationTask || operationTask.IsCompleted)
        {
            var result = await operationTask;
            TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] COMPLETE: {phase}");
            return result;
        }

        TestContext.WriteLine(
            $"[{DateTimeOffset.UtcNow:O}] TIMEOUT: {phase}; requesting cancellation.");

        // Cancellation callbacks can themselves block, so request cancellation on a worker and do
        // not let that request consume the remaining test timeout. The operation gets a short drain
        // window to release lifecycle leases and DbContexts.
        _ = Task.Run(() =>
        {
            try
            {
                cancellation.Cancel();
            }
            catch
            {
            }
        });

        try
        {
            await operationTask.WaitAsync(CancellationDrainTimeout);
        }
        catch
        {
            // The operation may finish by observing cancellation or may remain blocked. In either
            // case report the phase that exceeded the deadline rather than allowing fixture cleanup
            // to replace it with the outer MSTest timeout.
        }

        if (!operationTask.IsCompleted)
        {
            _ = operationTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        throw new AssertFailedException(
            $"Enrollment end-to-end phase '{phase}' did not complete within " +
            $"{EnrollmentPhaseTimeout.TotalSeconds:0} seconds. Cancellation was requested and " +
            $"the operation was given {CancellationDrainTimeout.TotalSeconds:0} seconds to unwind.");
    }

    private async Task RunPhaseAsync(
        string phase,
        Func<CancellationToken, Task> operation)
    {
        await RunPhaseAsync<object?>(
            phase,
            async ct =>
            {
                await operation(ct);
                return null;
            });
    }

    private static async Task SetEmptyDiagnosticsOnHealthySnapshotAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userId = (await users.ListUserIdsAsync()).Single();
        var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
        var rows = await snapshots.ListForUserAsync(userId);
        var healthy = rows.SingleOrDefault(row => row.Status == UserSyncSnapshotStatus.LocalPublished);
        if (healthy is null)
        {
            var user = await users.GetByIdAsync(userId)
                ?? throw new AssertFailedException("The registered source user could not be loaded.");
            healthy = await scope.ServiceProvider
                .GetRequiredService<IUserSnapshotPublisherService>()
                .GetOrCreateAsync(user);
        }

        healthy.QuarantineReason = string.Empty;
        healthy.ConflictingSnapshotHash = [];
        snapshots.Update(healthy);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static void Connect(
        ProductionEnrollmentTestHost source,
        ProductionEnrollmentTestHost target)
    {
        source.ConnectEnrollmentTransportTo(target);
    }

    private static async Task<DeviceEnrollmentCommit> GetOnlyEnrollmentCommitAsync(
        ProductionEnrollmentTestHost host)
    {
        var commits = await GetEnrollmentCommitsAsync(host);
        Assert.AreEqual(1, commits.Count);
        return commits[0];
    }

    private static async Task<IReadOnlyList<DeviceEnrollmentCommit>> GetEnrollmentCommitsAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userIds = await users.ListUserIdsAsync();
        Assert.AreEqual(1, userIds.Count);
        return await scope.ServiceProvider
            .GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(userIds[0]);
    }

    private static async Task<UserCanonicalCheckpoint?> GetOnlyCanonicalCheckpointAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var userIds = await scope.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .ListUserIdsAsync();
        Assert.AreEqual(1, userIds.Count);
        return await scope.ServiceProvider
            .GetRequiredService<IUserCanonicalCheckpointRepository>()
            .GetAsync(userIds[0]);
    }

    private static async Task<UserLoginIdentityState> GetOnlyLoginIdentityAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var identities = await scope.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .ListLoginIdentityStatesAsync();
        Assert.AreEqual(1, identities.Count);
        return identities[0];
    }

    private static async Task<User> GetOnlyUserAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userIds = await users.ListUserIdsAsync();
        Assert.AreEqual(1, userIds.Count);
        return await users.GetByIdAsync(userIds[0])
            ?? throw new AssertFailedException("The expected canonical user could not be loaded.");
    }

    private static async Task<IReadOnlyList<Guid>> GetUserIdsAsync(
        ProductionEnrollmentTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .ListUserIdsAsync();
    }
}
