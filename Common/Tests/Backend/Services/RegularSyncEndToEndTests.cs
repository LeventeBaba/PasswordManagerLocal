using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed partial class RegularSyncEndToEndTests
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const int ThreeDeviceTestTimeoutMilliseconds = 180_000;
    private const int MaxOneWaySessionsPerStep = 12;
    private const int MaxBidirectionalSessionsPerStep = 12;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task RemovedDevice_ReceivesSignedRevocation_LogsOutAndRelaysItToOtherDevices()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeOneWayUntilAsync(
            setup.Middle, setup.Original,
            "original receives third membership before removal",
            () => HasActiveMembershipForDeviceAsync(setup.Original, setup.NewDevice.Identity.LocalDeviceId));

        var removal = await setup.Original.Endpoints.DisconnectUserDeviceAsync(
            setup.OriginalToken, setup.Middle.Identity.LocalDeviceId,
            Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        Assert.IsTrue(removal.Removed);
        Assert.IsTrue(removal.OperationId.HasValue);
        var removalOperationId = removal.OperationId.Value;
        Assert.AreNotEqual(Guid.Empty, removalOperationId);
        Assert.IsFalse((await setup.Original.Endpoints.GetUserDevicesAsync(setup.OriginalToken))
            .Any(device => device.DeviceId == setup.Middle.Identity.LocalDeviceId));

        await SynchronizeOneWayUntilAsync(
            setup.Original, setup.Middle,
            "removed installation receives its signed revocation",
            async () => await HasAppliedRemovalAsync(setup.Middle, removalOperationId) &&
                !setup.Middle.Services.GetRequiredService<ITokenService>()
                    .TryGetUid(setup.MiddleToken, out _));

        await Assert.ThrowsExactlyAsync<InvalidTokenException>(async () =>
            await setup.Middle.Endpoints.GetSavedPasswordsAsync(setup.MiddleToken));
        await AssertLoginDeniedAsync(setup.Middle, setup.Username);

        await SynchronizeOneWayUntilAsync(
            setup.Middle, setup.NewDevice,
            "removed installation relays signed revocation to third device",
            () => HasAppliedRemovalAsync(setup.NewDevice, removalOperationId));

        Assert.IsFalse((await setup.NewDevice.Endpoints.GetUserDevicesAsync(setup.NewDeviceToken))
            .Any(device => device.DeviceId == setup.Middle.Identity.LocalDeviceId));
        Assert.IsTrue((await setup.Original.Endpoints.GetSavedPasswordsAsync(setup.OriginalToken))
            .Passwords.Any(password => password.Name == "Original device credential"));
        Assert.IsTrue((await setup.NewDevice.Endpoints.GetSavedPasswordsAsync(setup.NewDeviceToken))
            .Passwords.Any(password => password.Name == "Original device credential"));

        await using var restarted = await setup.Middle.RestartAsync();
        await AssertLoginDeniedAsync(restarted, setup.Username);
    }

    private static async Task AssertLoginDeniedAsync(ProductionSyncTestHost host, string username)
    {
        try
        {
            await host.Endpoints.LoginAsync(host.CreateLoginRequest(username));
            Assert.Fail("A removed installation must never issue another account session.");
        }
        catch (UnauthorizedAccessException) { }
        catch (UserNotFoundException) { }
    }

    private static async Task<bool> HasAppliedRemovalAsync(ProductionSyncTestHost host, Guid operationId)
    {
        using var scope = host.Services.CreateScope();
        var operation = await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
            .GetByIdAsync(operationId);
        return operation is
        {
            OperationType: UserControlOperationType.DeviceRemoval,
            Status: UserControlOperationStatus.Applied
        };
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task PostEnrollmentSyncBeforeFirstTargetLogin_BothDevicesCanAuthenticate()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(target);
        target.ConnectTo(source);

        var username = $"syncfirstlogin{Guid.NewGuid().ToString("N")[..12]}";
        var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.EnableSyncAsync(sourceToken);

        var enrollmentCode = await RunStepAsync(
            "target enrollment listener startup before realistic post-enrollment synchronization",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        await RunStepAsync(
            "production enrollment before either device reauthenticates",
            ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct));

        Assert.IsNotNull(
            await target.GetOnlyCanonicalCheckpointAsync(),
            "The imported account must be covered by a local signed checkpoint before automatic synchronization starts.");
        Assert.AreEqual(
            UserLoginIdentityStatus.Active,
            (await target.GetOnlyLoginIdentityAsync()).Status);

        await target.ActivateImportedSynchronizationAsync();
        var preLoginPair = new SyncPair(source, target, sourceToken, Guid.Empty);
        var sourceSnapshotInventoryCalls = source.Transport.SnapshotInventoryExchangeCalls;
        var targetSnapshotInventoryCalls = target.Transport.SnapshotInventoryExchangeCalls;
        await SynchronizeUntilAsync(
            preLoginPair,
            "automatic-style post-enrollment synchronization before first target login",
            async () =>
                source.Transport.SnapshotInventoryExchangeCalls > sourceSnapshotInventoryCalls &&
                target.Transport.SnapshotInventoryExchangeCalls > targetSnapshotInventoryCalls &&
                !await source.HasPendingForAsync(target.Identity.LocalDeviceId) &&
                !await target.HasPendingForAsync(source.Identity.LocalDeviceId));

        Assert.AreEqual(
            UserLoginIdentityStatus.Active,
            (await target.GetOnlyLoginIdentityAsync()).Status,
            "Post-enrollment synchronization must not invalidate the imported username projection.");

        await source.Endpoints.LogoutAsync(sourceToken);
        var sourceReloginToken = await RunStepAsync(
            "original device login after enrollment and post-enrollment synchronization",
            ct => source.Endpoints.LoginAsync(source.CreateLoginRequest(username), ct));
        var targetFirstLoginToken = await RunStepAsync(
            "new device first login after post-enrollment synchronization",
            ct => target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));

        Assert.AreEqual(username, (await source.Endpoints.GetUserProfileInfoAsync(sourceReloginToken)).Username);
        Assert.AreEqual(username, (await target.Endpoints.GetUserProfileInfoAsync(targetFirstLoginToken)).Username);

        await source.Endpoints.LogoutAsync(sourceReloginToken);
        await target.Endpoints.LogoutAsync(targetFirstLoginToken);
        Assert.AreNotEqual(
            Guid.Empty,
            await source.Endpoints.LoginAsync(source.CreateLoginRequest(username)));
        Assert.AreNotEqual(
            Guid.Empty,
            await target.Endpoints.LoginAsync(target.CreateLoginRequest(username)));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task RegularSyncBetweenTwoProductionBackends_TransfersAndMergesCompleteUserData()
    {
        await using var pair = await CreateEnrolledPairAsync();
        var expectedSecret = Encoding.UTF8.GetBytes("regular-sync-secret");
        var deltaSendsBeforeArrangement = pair.Source.Transport.DeltaSendCalls;

        await pair.Source.Endpoints.AddPasswordTagAsync(pair.SourceToken, new NewPasswordTagRequest
        {
            Name = "Regular sync tag",
            Color = "#FF123456"
        });
        await pair.Source.Endpoints.AddCustomUserColorsAsync(pair.SourceToken,
        [
            new NewCustomUserColorRequest
            {
                ColorName = "Regular sync color",
                ColorCode = "#FF654321"
            }
        ]);

        var sourceData = await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
        var sourceTag = sourceData.Tags.Single(tag => tag.Name == "Regular sync tag");
        var sourceColor = sourceData.CustomColors.Single(color => color.ColorName == "Regular sync color");

        await pair.Source.Endpoints.AddNewPasswordAsync(pair.SourceToken, new NewPasswordRequest
        {
            Name = "Regular sync password",
            Description = "Transferred through the ordinary synchronization pipeline.",
            Color = sourceColor.ColorCode,
            Password = expectedSecret,
            TagIds = [sourceTag.Id]
        });
        await pair.Source.Endpoints.UpdateUserProfileInfoAsync(new UpdateUserProfileRequest
        {
            Token = pair.SourceToken,
            NewEamil = "regular-sync-updated@example.com",
            newFirstName = "Updated",
            NewLastName = "ThroughSync"
        });

        Assert.AreEqual(
            deltaSendsBeforeArrangement,
            pair.Source.Transport.DeltaSendCalls,
            "Test data arrangement unexpectedly started regular synchronization before the explicit sync step.");

        await SynchronizeOneWayUntilAsync(
            pair.Source,
            pair.Target,
            "target receives the complete ordinary user-data update",
            async () =>
            {
                var targetData = await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
                var targetProfile = await pair.Target.Endpoints.GetUserProfileInfoAsync(pair.TargetToken);
                return targetData.Passwords.Any(password => password.Name == "Regular sync password") &&
                       targetData.Tags.Any(tag => tag.Name == "Regular sync tag") &&
                       targetData.CustomColors.Any(color => color.ColorName == "Regular sync color") &&
                       targetProfile.Email == "regular-sync-updated@example.com" &&
                       targetProfile.FirstName == "Updated" &&
                       targetProfile.LastName == "ThroughSync";
            });

        var imported = await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
        var importedPassword = imported.Passwords.Single(password => password.Name == "Regular sync password");
        var importedTag = imported.Tags.Single(tag => tag.Name == "Regular sync tag");
        var importedColor = imported.CustomColors.Single(color => color.ColorName == "Regular sync color");

        Assert.AreEqual(sourceTag.Id, importedTag.Id);
        Assert.AreEqual(sourceColor.Id, importedColor.Id);
        Assert.AreEqual(sourceColor.ColorCode, importedPassword.Color);
        CollectionAssert.AreEqual(new[] { sourceTag.Id }, importedPassword.TagIds.ToArray());
        CollectionAssert.AreEqual(
            expectedSecret,
            await pair.Target.Endpoints.GetUnsecurePasswordAsync(pair.TargetToken, importedPassword.Id));

        Assert.IsFalse(
            await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId),
            "The source queue was not acknowledged after the completed synchronization session.");
        Assert.IsTrue(pair.Source.Transport.DeltaSendCalls > 0);
        Assert.IsTrue(pair.Source.Transport.SnapshotInventoryExchangeCalls > 0);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task ConcurrentChangesOnBothDevices_ConvergeAfterBidirectionalRegularSync()
    {
        await using var pair = await CreateEnrolledPairAsync();
        var sourceSecret = Encoding.UTF8.GetBytes("source-side-secret");
        var targetSecret = Encoding.UTF8.GetBytes("target-side-secret");

        await pair.Source.Endpoints.AddNewPasswordAsync(pair.SourceToken, new NewPasswordRequest
        {
            Name = "Source concurrent password",
            Description = "Created on the original device.",
            Color = "#FF102030",
            Password = sourceSecret,
            TagIds = []
        });
        await pair.Target.Endpoints.AddNewPasswordAsync(pair.TargetToken, new NewPasswordRequest
        {
            Name = "Target concurrent password",
            Description = "Created on the enrolled device.",
            Color = "#FF304050",
            Password = targetSecret,
            TagIds = []
        });

        await SynchronizeUntilAsync(
            pair,
            "both devices converge after concurrent ordinary changes",
            async () =>
            {
                var sourceData = await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
                var targetData = await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
                var requiredNames = new[] { "Source concurrent password", "Target concurrent password" };
                return requiredNames.All(name => sourceData.Passwords.Any(password => password.Name == name)) &&
                       requiredNames.All(name => targetData.Passwords.Any(password => password.Name == name));
            });

        var convergedSource = await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
        var convergedTarget = await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
        Assert.AreEqual(2, convergedSource.Passwords.Count);
        Assert.AreEqual(2, convergedTarget.Passwords.Count);

        var sourcePasswordOnTarget = convergedTarget.Passwords.Single(password => password.Name == "Source concurrent password");
        var targetPasswordOnSource = convergedSource.Passwords.Single(password => password.Name == "Target concurrent password");
        CollectionAssert.AreEqual(
            sourceSecret,
            await pair.Target.Endpoints.GetUnsecurePasswordAsync(pair.TargetToken, sourcePasswordOnTarget.Id));
        CollectionAssert.AreEqual(
            targetSecret,
            await pair.Source.Endpoints.GetUnsecurePasswordAsync(pair.SourceToken, targetPasswordOnSource.Id));

        await SynchronizeUntilAsync(
            pair,
            "both regular-sync queues become idle after convergence",
            async () =>
                !await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId) &&
                !await pair.Target.HasPendingForAsync(pair.Source.Identity.LocalDeviceId));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task FailedRegularSync_KeepsDurableQueueAndRetryTransfersTheChange()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Endpoints.AddNewPasswordAsync(pair.SourceToken, new NewPasswordRequest
        {
            Name = "Retry after transport failure",
            Description = "The first regular sync send is intentionally rejected.",
            Color = "#FF506070",
            Password = Encoding.UTF8.GetBytes("retry-secret"),
            TagIds = []
        });

        var initialSendCalls = pair.Source.Transport.DeltaSendCalls;
        pair.Source.Transport.FailNextDeltaSend = true;
        await StartSyncAsync(pair.Source, pair.Target, "regular synchronization with simulated transport failure");

        Assert.IsTrue(
            pair.Source.Transport.DeltaSendCalls > initialSendCalls,
            "The simulated failed send was not attempted before the worker became idle.");
        Assert.IsTrue(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId));
        Assert.IsFalse((await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
            .Passwords.Any(password => password.Name == "Retry after transport failure"));

        await SynchronizeOneWayUntilAsync(
            pair.Source,
            pair.Target,
            "the retry transfers and acknowledges the retained change",
            async () =>
                (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
                    .Passwords.Any(password => password.Name == "Retry after transport failure") &&
                !await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId));

        Assert.IsTrue(pair.Source.Transport.DeltaSendCalls >= initialSendCalls + 2);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task PasswordDeletion_PropagatesAndDoesNotResurrectAfterRoundTripSync()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Endpoints.AddNewPasswordAsync(pair.SourceToken, new NewPasswordRequest
        {
            Name = "Regular sync tombstone password",
            Description = "Created before testing deletion propagation.",
            Color = "#FF8090A0",
            Password = Encoding.UTF8.GetBytes("deleted-secret"),
            TagIds = []
        });

        await SynchronizeOneWayUntilAsync(
            pair.Source,
            pair.Target,
            "target receives the password that will be deleted",
            async () => (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
                .Passwords.Any(password => password.Name == "Regular sync tombstone password"));

        var targetPassword = (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
            .Passwords.Single(password => password.Name == "Regular sync tombstone password");
        await pair.Target.Endpoints.RemovePasswordsAsync(pair.TargetToken, [targetPassword.Id]);

        await SynchronizeUntilAsync(
            pair,
            "the deletion tombstone removes the password from both devices",
            async () =>
                !(await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken))
                    .Passwords.Any(password => password.Id == targetPassword.Id) &&
                !(await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
                    .Passwords.Any(password => password.Id == targetPassword.Id));

        await Task.WhenAll(
            StartSyncAsync(pair.Source, pair.Target, "post-deletion source-to-target round trip"),
            StartSyncAsync(pair.Target, pair.Source, "post-deletion target-to-source round trip"));

        Assert.IsFalse((await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken))
            .Passwords.Any(password => password.Id == targetPassword.Id));
        Assert.IsFalse((await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
            .Passwords.Any(password => password.Id == targetPassword.Id));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task SnapshotAntiEntropy_RecoversChangeWhenOrdinaryQueueItemIsMissing()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Endpoints.AddNewPasswordAsync(pair.SourceToken, new NewPasswordRequest
        {
            Name = "Recovered by anti-entropy",
            Description = "The ordinary target queue entry is removed before synchronization.",
            Color = "#FF708090",
            Password = Encoding.UTF8.GetBytes("anti-entropy-secret"),
            TagIds = []
        });

        await pair.Source.PublishCurrentUserSnapshotAsync();
        var removed = await pair.Source.DeletePendingForAsync(pair.Target.Identity.LocalDeviceId);
        Assert.IsTrue(removed > 0, "The source change did not create a target queue item to remove.");
        Assert.IsFalse(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId));
        var inventoryCallsBefore = pair.Source.Transport.SnapshotInventoryExchangeCalls;

        await SynchronizeOneWayUntilAsync(
            pair.Source,
            pair.Target,
            "anti-entropy relays the missing snapshot",
            async () => (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken))
                .Passwords.Any(password => password.Name == "Recovered by anti-entropy"));

        Assert.IsTrue(pair.Source.Transport.SnapshotInventoryExchangeCalls > inventoryCallsBefore);
        Assert.IsTrue(pair.Source.Transport.DeltaSendCalls > 0);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task EnrollingThirdDevice_ExistingUninvolvedDeviceLearnsItsMembershipAndData()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();

        var devicesBeforeSecondEnrollmentSync = await RunStepAsync("read the original device membership before propagation",
            ct => setup.Original.Endpoints.GetUserDevicesAsync(setup.OriginalToken, ct));
        Assert.AreEqual(2, devicesBeforeSecondEnrollmentSync.Count);
        Assert.IsFalse(devicesBeforeSecondEnrollmentSync.Any(device =>
            device.DeviceId == setup.NewDevice.Identity.LocalDeviceId),
            "The first device must not learn about the third device merely because the second device enrolled it.");

        Assert.AreEqual(
            DeviceEnrollmentState.Completed,
            (await setup.NewDevice.Endpoints.GetDeviceEnrollmentStatusAsync()).State);
        AssertDeviceIds(
            (await setup.Middle.Endpoints.GetUserDevicesAsync(setup.MiddleToken)).Select(device => device.DeviceId),
            setup.Original.Identity.LocalDeviceId,
            setup.Middle.Identity.LocalDeviceId,
            setup.NewDevice.Identity.LocalDeviceId);
        AssertDeviceIds(
            (await setup.NewDevice.Endpoints.GetUserDevicesAsync(setup.NewDeviceToken)).Select(device => device.DeviceId),
            setup.Original.Identity.LocalDeviceId,
            setup.Middle.Identity.LocalDeviceId,
            setup.NewDevice.Identity.LocalDeviceId);

        var newDeviceSecret = Encoding.UTF8.GetBytes("third-device-created-secret");
        await RunStepAsync("write a credential on the newly enrolled third device",
            ct => AddPasswordAsync(setup.NewDevice, setup.NewDeviceToken, "Third device credential", newDeviceSecret, ct));

        var enrolledData = await setup.NewDevice.Endpoints.GetSavedPasswordsAsync(setup.NewDeviceToken);
        CollectionAssert.AreEqual(
            new[] { "Original device credential", "Second device credential", "Third device credential" },
            enrolledData.Passwords.Select(password => password.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        await SynchronizeOneWayUntilAsync(
            setup.Middle,
            setup.Original,
            "the uninvolved original device receives the third device membership",
            () => HasActiveMembershipForDeviceAsync(
                setup.Original,
                setup.NewDevice.Identity.LocalDeviceId));

        // Keep the original authenticated session alive across membership and snapshot arrival.
        var originalDevices = await setup.Original.Endpoints.GetUserDevicesAsync(setup.OriginalToken);
        AssertDeviceIds(
            originalDevices.Select(device => device.DeviceId),
            setup.Original.Identity.LocalDeviceId,
            setup.Middle.Identity.LocalDeviceId,
            setup.NewDevice.Identity.LocalDeviceId);

        await setup.Original.CacheEndpointForAsync(setup.NewDevice);
        await SynchronizeOneWayUntilAsync(
            setup.NewDevice,
            setup.Original,
            "the uninvolved original device receives data created on the newly enrolled device",
            async () => (await setup.Original.Endpoints.GetSavedPasswordsAsync(setup.OriginalToken))
                .Passwords.Any(password => password.Name == "Third device credential"));

        var receivedData = await setup.Original.Endpoints.GetSavedPasswordsAsync(setup.OriginalToken);
        Assert.IsTrue(receivedData.Passwords.Any(password => password.Name == "Second device credential"));
        var receivedPassword = receivedData.Passwords.Single(password => password.Name == "Third device credential");
        CollectionAssert.AreEqual(
            new[] { "Original device credential", "Second device credential", "Third device credential" },
            receivedData.Passwords.Select(password => password.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        CollectionAssert.AreEqual(
            newDeviceSecret,
            await setup.Original.Endpoints.GetUnsecurePasswordAsync(setup.OriginalToken, receivedPassword.Id));
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task ThreeEnrolledDevices_SyncInBothDirectionsAndSeeEachOtherOnline()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();

        await SynchronizeOneWayUntilAsync(
            setup.Middle,
            setup.Original,
            "all three device memberships are present on the original device",
            () => HasActiveMembershipForDeviceAsync(
                setup.Original,
                setup.NewDevice.Identity.LocalDeviceId));

        // All three existing sessions remain alive while the mesh converges.

        var originalOnlySecret = Encoding.UTF8.GetBytes("three-way-original-secret");
        var middleOnlySecret = Encoding.UTF8.GetBytes("three-way-middle-secret");
        var newDeviceOnlySecret = Encoding.UTF8.GetBytes("three-way-third-secret");
        await AddPasswordAsync(setup.Original, setup.OriginalToken, "Original-only credential", originalOnlySecret);
        await AddPasswordAsync(setup.Middle, setup.MiddleToken, "Second-only credential", middleOnlySecret);
        await AddPasswordAsync(setup.NewDevice, setup.NewDeviceToken, "Third-only credential", newDeviceOnlySecret);

        // Prime every authenticated endpoint so each of the six directed sessions uses the real
        // production endpoint registry and device-identity cache.
        await setup.Original.CacheEndpointForAsync(setup.Middle);
        await setup.Original.CacheEndpointForAsync(setup.NewDevice);
        await setup.Middle.CacheEndpointForAsync(setup.Original);
        await setup.Middle.CacheEndpointForAsync(setup.NewDevice);
        await setup.NewDevice.CacheEndpointForAsync(setup.Original);
        await setup.NewDevice.CacheEndpointForAsync(setup.Middle);

        var expectedCredentialNames = new[]
        {
            "Original device credential",
            "Second device credential",
            "Original-only credential",
            "Second-only credential",
            "Third-only credential"
        };

        // Give every directed device pair a distinct source credential to deliver. This proves the
        // original device, the device used for enrollment, and the newly enrolled device can all
        // initiate and receive ordinary synchronization sessions with each other.
        await SynchronizeOneWayUntilAsync(
            setup.Original,
            setup.Middle,
            "original-to-second device sync",
            () => HasPasswordAsync(setup.Middle, setup.MiddleToken, "Original-only credential"));
        await SynchronizeOneWayUntilAsync(
            setup.Middle,
            setup.NewDevice,
            "second-to-third device sync",
            () => HasPasswordAsync(setup.NewDevice, setup.NewDeviceToken, "Second-only credential"));
        await SynchronizeOneWayUntilAsync(
            setup.NewDevice,
            setup.Original,
            "third-to-original device sync",
            () => HasPasswordAsync(setup.Original, setup.OriginalToken, "Third-only credential"));
        await SynchronizeOneWayUntilAsync(
            setup.Original,
            setup.NewDevice,
            "original-to-third device sync",
            () => HasPasswordAsync(setup.NewDevice, setup.NewDeviceToken, "Original-only credential"));
        await SynchronizeOneWayUntilAsync(
            setup.NewDevice,
            setup.Middle,
            "third-to-second device sync",
            () => HasPasswordAsync(setup.Middle, setup.MiddleToken, "Third-only credential"));
        await SynchronizeOneWayUntilAsync(
            setup.Middle,
            setup.Original,
            "second-to-original device sync",
            () => HasPasswordAsync(setup.Original, setup.OriginalToken, "Second-only credential"));

        // Run an explicit session for every directed pair even when a change already arrived via
        // another peer. That verifies the direct authenticated link itself in both directions.
        foreach (var (source, target) in setup.DirectedPeerPairs)
        {
            await StartSyncAsync(
                source,
                target,
                $"explicit three-device mesh session from {source.Identity.LocalDeviceId} to {target.Identity.LocalDeviceId}");
        }

        foreach (var host in setup.Hosts)
        {
            var data = await host.Endpoints.GetSavedPasswordsAsync(setup.TokenFor(host));
            CollectionAssert.AreEquivalent(
                expectedCredentialNames,
                data.Passwords.Select(password => password.Name).ToArray());
        }

        foreach (var (source, target) in setup.DirectedPeerPairs)
        {
            Assert.IsFalse(
                await source.HasPendingForAsync(target.Identity.LocalDeviceId),
                $"The sync queue from {source.Identity.LocalDeviceId} to {target.Identity.LocalDeviceId} was not acknowledged.");
        }

        await AssertPasswordSecretAsync(
            setup.Original, setup.OriginalToken, "Second-only credential", middleOnlySecret);
        await AssertPasswordSecretAsync(
            setup.Original, setup.OriginalToken, "Third-only credential", newDeviceOnlySecret);
        await AssertPasswordSecretAsync(
            setup.Middle, setup.MiddleToken, "Original-only credential", originalOnlySecret);
        await AssertPasswordSecretAsync(
            setup.Middle, setup.MiddleToken, "Third-only credential", newDeviceOnlySecret);
        await AssertPasswordSecretAsync(
            setup.NewDevice, setup.NewDeviceToken, "Original-only credential", originalOnlySecret);
        await AssertPasswordSecretAsync(
            setup.NewDevice, setup.NewDeviceToken, "Second-only credential", middleOnlySecret);

        foreach (var host in setup.Hosts)
        {
            await host.Services.GetRequiredService<DevicePresencePollingHostedService>().StopAsync();
            host.Presence.InvalidateAll("arrange three-device authenticated online visibility test");
        }

        foreach (var (observer, remote) in setup.DirectedPeerPairs)
        {
            var remoteDevice = await observer.CacheEndpointForAsync(remote);
            var probe = await observer.ProbeAsync(remoteDevice, force: true);

            Assert.IsTrue(
                probe.IsSuccess,
                $"Authenticated presence probe from {observer.Identity.LocalDeviceId} to " +
                $"{remote.Identity.LocalDeviceId} failed ({probe.FailureKind}).");
            Assert.IsTrue(observer.IsOnline(remote));
            Assert.IsTrue(observer.Presence.TryGetSnapshot(remote.Identity.FingerprintHex, out var snapshot));
            // Another authenticated sync may race the successful direct probe and become
            // the most recent observation. The probe result above proves direct reachability.
            Assert.IsTrue(snapshot!.LastObservationSource is
                DevicePresenceObservationSource.DirectProbe or
                DevicePresenceObservationSource.IncomingSync or
                DevicePresenceObservationSource.OutgoingSync);
        }

        await AssertOnlineDevicesAsync(setup.Original,
            setup.OriginalToken,
            setup.Middle.Identity.LocalDeviceId,
            setup.NewDevice.Identity.LocalDeviceId);
        await AssertOnlineDevicesAsync(setup.Middle,
            setup.MiddleToken,
            setup.Original.Identity.LocalDeviceId,
            setup.NewDevice.Identity.LocalDeviceId);
        await AssertOnlineDevicesAsync(setup.NewDevice,
            setup.NewDeviceToken,
            setup.Original.Identity.LocalDeviceId,
            setup.Middle.Identity.LocalDeviceId);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SnapshotAcknowledgedDuringInitialReadGate_IsMergedWithoutAnotherTransportDelivery()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        Assert.IsTrue(pair.Target.OwnsPostLoginConsumer,
            "The production-composition fixture must own the deferred reconciliation consumer.");
        const string passwordName = "Deferred edit with no further transport";
        await AddPasswordAsync(pair.Source, pair.SourceToken, passwordName, Encoding.UTF8.GetBytes("deferred-sync-secret"));
        var delta = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        var sentBefore = pair.Source.Transport.DeltaSendCalls;
        var scheduler = pair.Target.Services.GetRequiredService<UserPostLoginWorkScheduler>();
        var lifecycle = pair.Target.Services.GetRequiredService<IUserLifecycleCoordinator>();

        await lifecycle.ExecuteAsync(delta.SnapshotUserId, async ct =>
        {
            using var scope = pair.Target.Services.CreateScope();
            var services = scope.ServiceProvider;
            var user = await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(delta.SnapshotUserId, ct)
                ?? throw new InvalidOperationException("The enrolled user is missing.");
            scheduler.DeferUntilInitialRead(user.UId, user.KeyEpoch, user.MembershipEpoch, pair.TargetToken);
            Assert.IsTrue(scheduler.IsInitialReadPending(user.UId));

            // The receiver commits the encrypted snapshot and acknowledges it while
            // the first-read gate still prevents its canonical merge.
            Assert.IsTrue(await pair.Source.Transport.SendDeltasAsync("127.0.0.1", 26688,
                pair.Target.Identity.FingerprintHex, [delta], ct));
            var stored = await services.GetRequiredService<IUserSyncSnapshotRepository>().GetAsync(user.UId,
                pair.Source.Identity.LocalDeviceId, pair.Source.Identity.OriginInstanceId, user.KeyEpoch, ct);
            Assert.IsNotNull(stored);
            Assert.AreEqual(delta.SnapshotOriginRevision, stored.OriginRevision);
            Assert.AreEqual(UserSyncSnapshotStatus.Pending, stored.Status);
            await pair.Source.DeletePendingForAsync(pair.Target.Identity.LocalDeviceId, ct);
            Assert.IsFalse(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId, ct));
            pair.Source.EndpointsCache.Clear();
            pair.Target.EndpointsCache.Clear();

            // Holding lifecycle ownership prevents the background worker from racing
            // this first projection, but the endpoint releases its normal read gate.
            var initial = await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken, ct);
            Assert.IsFalse(initial.Passwords.Any(password => password.Name == passwordName));
            Assert.IsFalse(scheduler.IsInitialReadPending(user.UId));
        });

        await RunStepAsync("deferred consumer merges an acknowledged snapshot without another delivery", async ct =>
        {
            while (!(await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken, ct)).Passwords
                       .Any(password => password.Name == passwordName))
                await Task.Delay(10, ct);
        });
        Assert.AreEqual(sentBefore + 1, pair.Source.Transport.DeltaSendCalls,
            "The consumer must finish already stored work without requiring the sender to retransmit.");
        using var verification = pair.Target.Services.CreateScope();
        var knowledge = await verification.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
            .GetAsync(delta.SnapshotUserId, pair.Source.Identity.LocalDeviceId,
                pair.Source.Identity.OriginInstanceId, 1);
        Assert.IsNotNull(knowledge);
        Assert.IsTrue(knowledge.HighestMergedRevision >= delta.SnapshotOriginRevision);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReusedOutgoingBuilder_DoesNotReusePublicationStateFromBeforeOtherScopeCommits()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var retainedScope = pair.Source.Services.CreateScope();
        var services = retainedScope.ServiceProvider;
        var userId = (await services.GetRequiredService<IUserRepository>().ListUserIdsAsync()).Single();
        var peer = await services.GetRequiredService<IDeviceRepository>()
            .GetByIdWithUserDevicesAsync(pair.Target.Identity.LocalDeviceId)
            ?? throw new InvalidOperationException("The enrolled target is missing.");
        var builder = services.GetRequiredService<IOutgoingDeltaBuilderService>();
        var item = new SyncItem { ModelId = userId, ModelType = SyncModelType.User, ChangeType = SyncChangeType.Updated };
        var first = await builder.BuildAsync(item, peer);

        // Two independent commits/publications make stale sequence reuse observable
        // without depending on timestamp resolution or thread scheduling.
        await AddPasswordAsync(pair.Source, pair.SourceToken, "Independent publication 1", Encoding.UTF8.GetBytes("source-one-secret"));
        await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        await AddPasswordAsync(pair.Source, pair.SourceToken, "Independent publication 2", Encoding.UTF8.GetBytes("source-two-secret"));
        await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        long committedRevision;
        byte[] committedHash;
        using (var fresh = pair.Source.Services.CreateScope())
        {
            var publication = await fresh.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetLatestAsync(userId, 1)
                ?? throw new InvalidOperationException("The current publication is missing.");
            committedRevision = publication.OriginRevision;
            committedHash = publication.SnapshotHash.ToArray();
        }
        Assert.IsTrue(committedRevision >= first.SnapshotOriginRevision + 2);

        var second = await builder.BuildAsync(item, peer);
        Assert.AreEqual(committedRevision, second.SnapshotOriginRevision,
            "A reused transport builder must reuse the latest committed immutable publication, not an old origin sequence.");
        CollectionAssert.AreEqual(committedHash, second.SnapshotHash);
        using var receivingScope = pair.Target.Services.CreateScope();
        await receivingScope.ServiceProvider.GetRequiredService<IIncomingDeltaApplierService>().ApplyAsync(second);
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "Independent publication 1"));
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "Independent publication 2"));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReusedIncomingApplier_PreservesLocalCommitBetweenTwoRemoteDeltas()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var retainedScope = pair.Target.Services.CreateScope();
        var services = retainedScope.ServiceProvider;
        var userId = (await services.GetRequiredService<IUserRepository>().ListUserIdsAsync()).Single();
        // Seed the outer transport scope with an older tracked generation deliberately.
        Assert.IsNotNull(await services.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(userId));
        var applier = services.GetRequiredService<IIncomingDeltaApplierService>();
        await AddPasswordAsync(pair.Source, pair.SourceToken, "First remote edit", Encoding.UTF8.GetBytes("remote-one-secret"));
        await applier.ApplyAsync(await BuildLatestUserDeltaAsync(pair.Source, pair.Target));
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "First remote edit"));

        await AddPasswordAsync(pair.Target, pair.TargetToken, "Local edit between receives", Encoding.UTF8.GetBytes("target-local-secret"));
        await AddPasswordAsync(pair.Source, pair.SourceToken, "Second remote edit", Encoding.UTF8.GetBytes("remote-two-secret"));
        Assert.IsFalse(await HasPasswordAsync(pair.Source, pair.SourceToken, "Local edit between receives"),
            "The second remote snapshot must not already contain the local edit this regression protects.");
        var result = await applier.ApplyAsync(await BuildLatestUserDeltaAsync(pair.Source, pair.Target));

        Assert.IsNotNull(result.UserSnapshotReceipt);
        Assert.AreEqual(UserSnapshotReceiptState.MergedImmediately, result.UserSnapshotReceipt.State);
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "First remote edit"));
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "Second remote edit"));
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, "Local edit between receives"),
            "Merging through a reused transport service must start from the current committed local generation.");
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SyncHello_BindsClaimedIdentityToConnectedCertificate()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        pair.Target.Presence.InvalidateAll("Start the test without prior authenticated presence.");

        var penaltyCountBefore = pair.Target.PeerPenaltyAttempts.Count;
        var genuine = CreateHelloRequest(pair.Source.Identity);
        var unaffiliatedFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var unaffiliated = await pair.Target.ProtocolHandler.HelloAsync(genuine,
            new PeerConnectionContext { ClientCertificateFingerprint = unaffiliatedFingerprint }, CancellationToken.None);
        Assert.IsFalse(unaffiliated.Ok);
        Assert.AreEqual(penaltyCountBefore, pair.Target.PeerPenaltyAttempts.Count);
        Assert.IsFalse(pair.Target.IsOnline(pair.Source));

        var wrongSigningKey = genuine.Clone();
        wrongSigningKey.SignPub = ByteString.CopyFrom(pair.Target.Identity.SignPublicKey);
        var wrongDeviceId = genuine.Clone();
        wrongDeviceId.DeviceId = pair.Target.Identity.DeviceIdHex;
        foreach (var request in new[] { wrongSigningKey, wrongDeviceId })
        {
            var rejected = await pair.Target.ProtocolHandler.HelloAsync(request,
                new PeerConnectionContext { ClientCertificateFingerprint = pair.Source.Identity.FingerprintHex },
                CancellationToken.None);
            Assert.IsFalse(rejected.Ok);
        }

        CollectionAssert.AreEqual(
            new[] { pair.Source.Identity.LocalDeviceId, pair.Source.Identity.LocalDeviceId },
            pair.Target.PeerPenaltyAttempts.Skip(penaltyCountBefore).Select(attempt => attempt.PeerId).ToArray());
        Assert.IsFalse(pair.Target.IsOnline(pair.Source));

        await AcceptHelloAsync(pair.Source, pair.Target);
        Assert.IsTrue(pair.Target.IsOnline(pair.Source));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task IncomingSnapshot_WrongRecipientAndInvalidAuthenticationLeaveNoStoredSnapshot()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        const string name = "Authenticated incoming snapshot";
        var secret = Encoding.UTF8.GetBytes("test-only-vault-secret");
        await AddPasswordAsync(pair.Source, pair.SourceToken, name, secret);
        var genuine = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        var before = await pair.Target.GetOnlyCanonicalCheckpointAsync();
        Assert.IsNotNull(before);

        var wrongRecipient = DeltaMapping.FromProto(DeltaMapping.ToProto(genuine));
        wrongRecipient.RecipientDeviceId = pair.Source.Identity.LocalDeviceId.ToString("N");
        NetDeltaSigner.FillSignature(wrongRecipient, pair.Source.Identity);
        var wrongRecipientFailure = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
            DeliverDeltaAsync(pair, wrongRecipient));
        Assert.AreEqual(SyncProtocolStatusCode.PermissionDenied, wrongRecipientFailure.StatusCode);

        var tamperedSignature = DeltaMapping.FromProto(DeltaMapping.ToProto(genuine));
        tamperedSignature.Sig[0] ^= 1;
        var signatureFailure = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
            DeliverDeltaAsync(pair, tamperedSignature));
        Assert.AreEqual(SyncProtocolStatusCode.InvalidArgument, signatureFailure.StatusCode);

        var tamperedTag = DeltaMapping.FromProto(DeltaMapping.ToProto(genuine));
        tamperedTag.Tag[0] ^= 1;
        // A valid outer signature cannot replace authenticated encryption of the payload.
        NetDeltaSigner.FillSignature(tamperedTag, pair.Source.Identity);
        var tagFailure = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
            DeliverDeltaAsync(pair, tamperedTag));
        Assert.AreEqual(SyncProtocolStatusCode.InvalidArgument, tagFailure.StatusCode);

        var forgedOrigin = BuildSnapshotWithInvalidOriginSignature(pair, genuine);
        var originFailure = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
            DeliverDeltaAsync(pair, forgedOrigin));
        Assert.AreEqual(SyncProtocolStatusCode.InvalidArgument, originFailure.StatusCode);
        StringAssert.Contains(originFailure.Message, "origin signature");

        Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, name));
        var after = await pair.Target.GetOnlyCanonicalCheckpointAsync();
        Assert.IsNotNull(after);
        CollectionAssert.AreEqual(before.CanonicalContentHash, after.CanonicalContentHash);
        using (var scope = pair.Target.Services.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
            Assert.IsNull(await inbox.GetExactAsync(genuine.SnapshotUserId, genuine.SnapshotOriginDeviceId,
                genuine.SnapshotOriginInstanceId, before.KeyEpoch, genuine.SnapshotOriginRevision));
        }

        var accepted = await DeliverDeltaAsync(pair, genuine);
        var receipt = accepted.UserSnapshotReceipts.Single();
        Assert.AreEqual(UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately, receipt.State);
        Assert.AreEqual(genuine.SnapshotOriginRevision, receipt.OriginRevision);
        CollectionAssert.AreEqual(genuine.SnapshotHash, receipt.SnapshotHash.ToByteArray());
        await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, name, secret);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task IncomingSnapshot_ReplayingAnOlderSignedRevisionCannotResurrectDeletedPassword()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        const string name = "Password deleted before replay";
        await AddPasswordAsync(pair.Source, pair.SourceToken, name, Encoding.UTF8.GetBytes("test-only-deleted-secret"));
        var oldDelta = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);

        var initial = await DeliverDeltaAsync(pair, oldDelta);
        Assert.AreEqual(UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately,
            initial.UserSnapshotReceipts.Single().State);
        Assert.IsTrue(await HasPasswordAsync(pair.Target, pair.TargetToken, name));

        var sourcePassword = (await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken))
            .Passwords.Single(password => password.Name == name);
        await pair.Source.Endpoints.RemovePasswordsAsync(pair.SourceToken, [sourcePassword.Id]);
        var newerDelta = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        Assert.IsTrue(newerDelta.SnapshotOriginRevision > oldDelta.SnapshotOriginRevision);
        var deletion = await DeliverDeltaAsync(pair, newerDelta);
        Assert.AreEqual(UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately,
            deletion.UserSnapshotReceipts.Single().State);
        Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, name));

        var beforeReplay = await pair.Target.GetOnlyCanonicalCheckpointAsync();
        Assert.IsNotNull(beforeReplay);
        var replay = await DeliverDeltaAsync(pair, oldDelta);
        var receipt = replay.UserSnapshotReceipts.Single();
        Assert.AreEqual(UserSnapshotReceiptStateProto.UserSnapshotReceiptObsoleteRevision, receipt.State);
        Assert.AreEqual(oldDelta.SnapshotOriginRevision, receipt.OriginRevision);
        Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, name));
        var afterReplay = await pair.Target.GetOnlyCanonicalCheckpointAsync();
        Assert.IsNotNull(afterReplay);
        CollectionAssert.AreEqual(beforeReplay.CanonicalContentHash, afterReplay.CanonicalContentHash);

        using var scope = pair.Target.Services.CreateScope();
        var retained = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetAsync(
            newerDelta.SnapshotUserId, newerDelta.SnapshotOriginDeviceId, newerDelta.SnapshotOriginInstanceId, beforeReplay.KeyEpoch);
        Assert.IsNotNull(retained);
        Assert.AreEqual(newerDelta.SnapshotOriginRevision, retained.OriginRevision);
        CollectionAssert.AreEqual(newerDelta.SnapshotHash, retained.SnapshotHash);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnrolledPeer_CannotRequestSnapshotForAnotherProfileEvenWithExactMetadata()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var shared = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        var privateToken = await pair.Source.Endpoints.RegisterAsync(pair.Source.CreateRegistrationRequest("private_vault"));
        await pair.Source.EnableSyncAsync(privateToken);
        await AddPasswordAsync(pair.Source, privateToken, "Unshared entry", Encoding.UTF8.GetBytes("test-only-unshared-secret"));

        UserSnapshotRequest privateRequest;
        UserSnapshotRequest sharedRequest;
        using (var scope = pair.Source.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<IUserRepository>();
            var publisher = services.GetRequiredService<IUserSnapshotPublisherService>();
            var sharedUser = await users.GetByIdAsync(shared.SnapshotUserId);
            var privateUserId = (await users.ListUserIdsAsync()).Single(id => id != shared.SnapshotUserId);
            var privateUser = await users.GetByIdAsync(privateUserId);
            Assert.IsNotNull(sharedUser);
            Assert.IsNotNull(privateUser);
            sharedRequest = CreateSnapshotRequest(await publisher.GetOrCreateAsync(sharedUser));
            privateRequest = CreateSnapshotRequest(await publisher.GetOrCreateAsync(privateUser));
        }

        var context = await AcceptHelloAsync(pair.Target, pair.Source);
        var mixed = new UserSnapshotRequestBatch();
        mixed.Requests.Add(sharedRequest);
        mixed.Requests.Add(privateRequest);
        var denied = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
            pair.Source.ProtocolHandler.RequestUserSnapshotsAsync(mixed, context, CancellationToken.None));
        Assert.AreEqual(SyncProtocolStatusCode.PermissionDenied, denied.StatusCode);

        var allowed = new UserSnapshotRequestBatch();
        allowed.Requests.Add(sharedRequest);
        var retryContext = await AcceptHelloAsync(pair.Target, pair.Source);
        var reply = await pair.Source.ProtocolHandler.RequestUserSnapshotsAsync(allowed, retryContext, CancellationToken.None);
        var returned = reply.Single();
        Assert.AreEqual(shared.SnapshotUserId, returned.SnapshotUserId);
        Assert.AreEqual(pair.Target.Identity.LocalDeviceId.ToString("N"), returned.RecipientDeviceId);
        Assert.IsTrue(returned.Payload.Length > 0);
    }

    private static HelloRequest CreateHelloRequest(IDeviceIdentityService identity) => new()
    {
        DatabaseVersion = DatabaseConstants.CurrentDbVersion,
        ProtocolVersion = SyncConstants.SyncProtocolVersion,
        DeviceId = identity.DeviceIdHex,
        SignPub = ByteString.CopyFrom(identity.SignPublicKey)
    };

    private static async Task<PeerConnectionContext> AcceptHelloAsync(
        ProductionSyncTestHost sender, ProductionSyncTestHost receiver, CancellationToken ct = default)
    {
        var request = CreateHelloRequest(sender.Identity);
        var context = new PeerConnectionContext
        {
            ClientCertificateFingerprint = sender.Identity.FingerprintHex,
            RemoteIpAddress = "127.0.0.1"
        };
        var reply = await receiver.ProtocolHandler.HelloAsync(request, context, ct);
        Assert.IsTrue(reply.Ok, "The enrolled device must authenticate before exercising the incoming protocol.");
        context.SyncHelloAccepted = true;
        context.RemoteDatabaseVersion = request.DatabaseVersion;
        context.RemoteProtocolVersion = request.ProtocolVersion;
        return context;
    }

    private static NetworkDelta BuildSnapshotWithInvalidOriginSignature(SyncPair pair, NetworkDelta genuine)
    {
        var receiver = pair.Target.Identity;
        var plaintext = receiver.DecryptFromDevice(genuine.Payload, genuine.EphemeralPublicKey,
            genuine.Nonce, genuine.Tag, SyncCryptoUtil.BuildAssociatedData(genuine));
        SyncDeltaPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(plaintext, BackendJsonSerializerContext.Default.SyncDeltaPayload)
                ?? throw new AssertFailedException("The genuine snapshot payload is missing.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        Assert.IsNotNull(payload.UserSnapshot);
        payload.UserSnapshot.OriginSignature[0] ^= 1;
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, genuine.Ts);

        // Re-encrypt and sign the transport envelope as the enrolled sender.
        // This leaves only the separately authenticated snapshot signature invalid.
        var delta = DeltaMapping.FromProto(DeltaMapping.ToProto(genuine));
        var modifiedPlaintext = JsonSerializer.SerializeToUtf8Bytes(payload, BackendJsonSerializerContext.Default.SyncDeltaPayload);
        try
        {
            delta.PayloadHash = SHA256.HashData(modifiedPlaintext);
            delta.Payload = pair.Source.Identity.EncryptForDevice(modifiedPlaintext, receiver.AgreementPublicKey,
                SyncCryptoUtil.BuildAssociatedData(delta), out var ephemeralKey, out var nonce, out var tag);
            delta.EphemeralPublicKey = ephemeralKey;
            delta.Nonce = nonce;
            delta.Tag = tag;
            NetDeltaSigner.FillSignature(delta, pair.Source.Identity);
            return delta;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(modifiedPlaintext);
        }
    }

    private static async Task<Ack> DeliverDeltaAsync(SyncPair pair, NetworkDelta delta, CancellationToken ct = default)
    {
        var context = await AcceptHelloAsync(pair.Source, pair.Target, ct);
        return await pair.Target.ProtocolHandler.PushDeltaAsync(SingleDeltaAsync(delta), context, ct);
    }

    private static async IAsyncEnumerable<DeltaChunk> SingleDeltaAsync(NetworkDelta delta)
    {
        yield return DeltaMapping.ToProto(delta);
        await Task.Yield();
    }

    private static UserSnapshotRequest CreateSnapshotRequest(UserSyncSnapshot snapshot) => new()
    {
        UserId = snapshot.UserId.ToString("N"),
        OriginDeviceId = snapshot.OriginDeviceId.ToString("N"),
        OriginInstanceId = snapshot.OriginInstanceId.ToString("N"),
        UserKeyEpoch = snapshot.UserKeyEpoch,
        MembershipEpoch = snapshot.MembershipEpoch,
        OriginRevision = snapshot.OriginRevision,
        ExpectedSnapshotHash = ByteString.CopyFrom(snapshot.SnapshotHash)
    };

    private static async Task PauseAutomaticDeliveryAsync(SyncPair pair, CancellationToken ct = default)
    {
        pair.Source.EndpointsCache.Clear();
        pair.Target.EndpointsCache.Clear();
        await pair.Source.WaitForSyncIdleAsync(pair.Target.Identity.LocalDeviceId, ct);
        await pair.Target.WaitForSyncIdleAsync(pair.Source.Identity.LocalDeviceId, ct);
        pair.Source.EndpointsCache.Clear();
        pair.Target.EndpointsCache.Clear();
        // Release initial login projection gates before testing synchronous merge admission.
        await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken, ct);
        await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken, ct);
    }

    private static async Task<NetworkDelta> BuildLatestUserDeltaAsync(
        ProductionSyncTestHost source, ProductionSyncTestHost target, Guid? forUser = null, CancellationToken ct = default)
    {
        using var scope = source.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userId = forUser ?? (await services.GetRequiredService<IUserRepository>().ListUserIdsAsync(ct)).Single();
        var peer = await services.GetRequiredService<IDeviceRepository>().GetByIdWithUserDevicesAsync(target.Identity.LocalDeviceId, ct)
            ?? throw new InvalidOperationException("The enrolled target is missing.");
        return await services.GetRequiredService<IOutgoingDeltaBuilderService>().BuildAsync(
            new SyncItem { ModelId = userId, ModelType = SyncModelType.User, ChangeType = SyncChangeType.Updated }, peer, ct);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task ThreeDevices_DiscoverAutomatically_KeepOriginalSessions_AndLoginAfterRestart()
    {
        await using var setup = await CreateThreeDeviceSetupAsync(automaticDiscovery: true);
        // A did not participate in B -> C enrollment. No endpoint seeding or StartSync calls.
        await WaitForAutomaticAsync(async () => await HasActiveMembershipForDeviceAsync(setup.Original, setup.NewDevice.Identity.LocalDeviceId));
        for (var round = 0; round < 3; round++)
        {
            await setup.NewDevice.Endpoints.SetLocalDeviceNameAsync(setup.NewDeviceToken, $"Third PC {round}");
            await AddPasswordAsync(setup.Middle, setup.MiddleToken, $"B edit {round}", Encoding.UTF8.GetBytes($"BSecret{round}Password!"));
            await AddPasswordAsync(setup.NewDevice, setup.NewDeviceToken, $"C edit {round}", Encoding.UTF8.GetBytes($"CSecret{round}Password!"));
            await setup.Original.Endpoints.GetUserDevicesAsync(setup.OriginalToken);
        }
        await WaitForAutomaticAsync(async () =>
        {
            foreach (var host in setup.Hosts)
            {
                var token = setup.TokenFor(host);
                if (!await HasPasswordAsync(host, token, "B edit 2") || !await HasPasswordAsync(host, token, "C edit 2")) return false;
                var devices = await host.Endpoints.GetUserDevicesAsync(token);
                if (!devices.Any(x => x.DeviceId == setup.NewDevice.Identity.LocalDeviceId && x.Name == "Third PC 2")) return false;
                var penalized = devices.Where(x => x.IsBlocked || x.InvalidSyncAttemptCount != 0).ToArray();
                Assert.IsFalse(penalized.Length != 0,
                    $"Unexpected peer penalties on {host.Identity.LocalDeviceId}: " +
                    string.Join(", ", penalized.Select(x => $"{x.DeviceId}: count={x.InvalidSyncAttemptCount}, blocked={x.IsBlocked}, reason={x.BlockedReason}")) +
                    "; recorded attempts: " +
                    string.Join("; ", host.PeerPenaltyAttempts.Select(x => $"{x.PeerId}: {x.Reason}")));
            }
            return true;
        }, failureDiagnostics: () => DescribeAutomaticConvergenceAsync(setup));
        // Passwords and device names can converge before A and C learn each other's
        // addresses through B's authenticated endpoint hints. Unlike the adjacent peers,
        // A and C cannot exchange multicast announcements in this scenario. Wait for
        // both relayed endpoints rather than assuming they arrived with the last edit.
        // Do not inject endpoints or start a direct synchronization session here: that
        // would bypass the automatic discovery path this regression test protects.
        TestContext.WriteLine("Waiting for A and C to discover each other through B's authenticated endpoint hints.");
        await WaitForAutomaticAsync(
            () => Task.FromResult(
                setup.Original.EndpointsCache.TryGetByFingerprint(setup.NewDevice.Identity.FingerprintHex, out _) &&
                setup.NewDevice.EndpointsCache.TryGetByFingerprint(setup.Original.Identity.FingerprintHex, out _)),
            "A and C did not both learn each other's endpoints through authenticated relay via B.",
            failureDiagnostics: () => DescribeAutomaticConvergenceAsync(setup));
        Assert.IsTrue(setup.Original.EndpointsCache.TryGetByFingerprint(setup.NewDevice.Identity.FingerprintHex, out _));
        Assert.IsTrue(setup.NewDevice.EndpointsCache.TryGetByFingerprint(setup.Original.Identity.FingerprintHex, out _));
        foreach (var host in setup.Hosts)
        {
            await using var restarted = await host.RestartAsync();
            Guid token;
            try { token = await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(setup.Username)); }
            catch
            {
                try { TestContext.WriteLine(await DescribeLoginSecurityStateAsync(restarted)); }
                catch (Exception diagnosticFailure) { TestContext.WriteLine($"Security diagnostics failed: {diagnosticFailure.GetType().Name}."); }
                throw;
            }
            Assert.IsTrue(await HasPasswordAsync(restarted, token, "B edit 2"));
            Assert.IsTrue(await HasPasswordAsync(restarted, token, "C edit 2"));
            Assert.IsTrue((await restarted.Endpoints.GetUserDevicesAsync(token)).Any(x => x.Name == "Third PC 2"));
        }
    }

    private static async Task<string> DescribeLoginSecurityStateAsync(ProductionSyncTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var lines = new List<string> { $"Restart login rejected on device {host.Identity.LocalDeviceId:N}." };
        foreach (var userId in await services.GetRequiredService<IUserRepository>().ListUserIdsAsync())
        {
            var state = await services.GetRequiredService<IUserControlStateRepository>().GetAsync(userId);
            lines.Add($"User={userId:N}, KeyEpoch={state?.AppliedKeyEpoch}, MembershipEpoch={state?.AppliedMembershipEpoch}, ControlConflict={state?.HasConflict}.");
            foreach (var operation in await services.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                lines.Add($"Operation={operation.OperationId:N}, Type={operation.OperationType}, Status={operation.Status}, " +
                    $"KeyEpoch={operation.PreviousKeyEpoch}->{operation.ResultingKeyEpoch}, MembershipEpoch={operation.PreviousMembershipEpoch}->{operation.ResultingMembershipEpoch}, Reason={operation.StatusReason}.");
            foreach (var fault in await services.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(userId))
                lines.Add($"Fault={fault.Kind}, Status={fault.Status}, BlocksLogin={fault.BlocksLogin}, BlocksLifecycle={fault.BlocksLifecycle}, Code={fault.DiagnosticCode}.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task WaitForAutomaticAsync(
        Func<Task<bool>> condition,
        string? failureMessage = null,
        Func<Task<string>>? failureDiagnostics = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        while (!await condition())
        {
            try
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                var message = failureMessage ?? "Automatic synchronization did not converge within 70 seconds.";
                if (failureDiagnostics is not null)
                {
                    try { message += Environment.NewLine + await failureDiagnostics(); }
                    catch (Exception exception) { message += $" Diagnostics failed: {exception.GetType().Name}."; }
                }
                Assert.Fail(message);
            }
        }
    }

    private static async Task<string> DescribeAutomaticConvergenceAsync(ThreeDeviceSetup setup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lines = new List<string>();
        foreach (var host in setup.Hosts)
        {
            var prefix = $"Device={host.Identity.LocalDeviceId:N}";
            try
            {
                var token = setup.TokenFor(host);
                var saved = await host.Endpoints.GetSavedPasswordsAsync(token, timeout.Token);
                var devices = await host.Endpoints.GetUserDevicesAsync(token, timeout.Token);
                var pendingTargets = new List<string>();
                using var scope = host.Services.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>();
                var faults = scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>();
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var faultDescriptions = new List<string>();
                var snapshotDescriptions = new List<string>();
                var knowledgeDescriptions = new List<string>();
                var initialReadGates = new List<string>();
                var hintRoutes = new List<string>();
                var userDevices = scope.ServiceProvider.GetRequiredService<IUserDeviceRepository>();
                var localUsers = scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
                var peers = setup.Hosts.Where(peer => !ReferenceEquals(peer, host)).ToArray();
                var postLogin = scope.ServiceProvider.GetRequiredService<IUserPostLoginWorkScheduler>();
                foreach (var userId in await users.ListUserIdsAsync(timeout.Token))
                {
                    initialReadGates.Add($"{userId:N}={postLogin.IsInitialReadPending(userId)}");
                    var localSyncOn = await localUsers.IsSyncOnAsync(userId, timeout.Token);
                    var links = (await userDevices.ListByUserWithDevicesAsync(userId, timeout.Token))
                        .ToDictionary(link => link.DeviceId);
                    foreach (var peer in peers)
                    {
                        var peerId = peer.Identity.LocalDeviceId;
                        var link = links.GetValueOrDefault(peerId);
                        var device = link?.Device;
                        var fingerprintMatches = device is not null && string.Equals(
                            FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint),
                            FingerprintUtil.NormalizeOrEmpty(peer.Identity.FingerprintHex), StringComparison.OrdinalIgnoreCase);
                        hintRoutes.Add($"{userId:N}/{peerId:N}/LocalSync={localSyncOn}/" +
                            $"LinkSync={link?.IsSyncOn}/Deleted={link?.IsDeleted}/LinkValid={link?.IsIntegrityValid()}/" +
                            $"Trusted={device?.IsTrusted}/Blocked={device?.IsBlocked}/DeviceValid={device?.IsIntegrityValid()}/" +
                            $"FingerprintMatches={fingerprintMatches}");
                    }
                    foreach (var fault in await faults.ListActiveForUserAsync(userId, timeout.Token))
                        faultDescriptions.Add($"{fault.Kind}/{fault.Status}/{fault.DiagnosticCode}/BlocksLogin={fault.BlocksLogin}");
                    foreach (var snapshot in await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId, timeout.Token))
                        snapshotDescriptions.Add($"{snapshot.OriginDeviceId:N}/r{snapshot.OriginRevision}/{snapshot.Status}/k{snapshot.UserKeyEpoch}/m{snapshot.MembershipEpoch}");
                    foreach (var knowledge in await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>().ListForUserAsync(userId, timeout.Token))
                        knowledgeDescriptions.Add($"{knowledge.OriginDeviceId:N}/stored={knowledge.HighestStoredRevision}/merged={knowledge.HighestMergedRevision}");
                }
                var discoveredPeers = peers.Select(peer =>
                {
                    var fingerprint = peer.Identity.FingerprintHex;
                    var cached = host.EndpointsCache.TryGetByFingerprint(fingerprint, out _);
                    var recent = host.EndpointsCache.IsRecentlyDiscovered(fingerprint, TimeSpan.FromMinutes(2));
                    return $"{peer.Identity.LocalDeviceId:N}={cached}/Recent={recent}";
                });
                var inventoryExchanges = peers.Select(peer => $"{peer.Identity.LocalDeviceId:N}/" +
                    host.Transport.DescribeSnapshotInventoryExchanges(peer.Identity.FingerprintHex));
                var sendFailures = new List<string>();
                foreach (var peer in peers)
                {
                    var lastFailure = host.Transport.GetLastDeltaSendFailure(peer.Identity.FingerprintHex);
                    if (lastFailure is not null)
                        sendFailures.Add($"{peer.Identity.LocalDeviceId:N}={lastFailure}");
                    if (await queue.HasPendingForDeviceAsync(peer.Identity.LocalDeviceId, timeout.Token))
                        pendingTargets.Add(peer.Identity.LocalDeviceId.ToString("N"));
                }
                lines.Add($"{prefix}; HasBEdit2={saved.Passwords.Any(item => item.Name == "B edit 2")}; " +
                    $"HasCEdit2={saved.Passwords.Any(item => item.Name == "C edit 2")}; " +
                    $"HasThirdName2={devices.Any(item => item.DeviceId == setup.NewDevice.Identity.LocalDeviceId && item.Name == "Third PC 2")}; " +
                    $"PendingTargets=[{string.Join(",", pendingTargets)}]; " +
                    $"PenalizedPeers={devices.Count(item => item.IsBlocked || item.InvalidSyncAttemptCount != 0)}; " +
                    $"PenaltyAttempts={host.PeerPenaltyAttempts.Count}; " +
                    $"Endpoints=[{string.Join(",", discoveredPeers)}]; " +
                    $"HintRoutes=[{string.Join(",", hintRoutes)}]; " +
                    $"InventoryExchanges=[{string.Join(",", inventoryExchanges)}]; " +
                    $"Faults=[{string.Join(",", faultDescriptions)}]; " +
                    $"Snapshots=[{string.Join(",", snapshotDescriptions)}]; " +
                    $"Knowledge=[{string.Join(",", knowledgeDescriptions)}]; " +
                    $"LastSendFailures=[{string.Join(",", sendFailures)}]; " +
                    $"PostLoginConsumerOwned={host.OwnsPostLoginConsumer}; InitialReadGates=[{string.Join(",", initialReadGates)}]; " +
                    $"DeltaSends={host.Transport.DeltaSendCalls}; Inventories={host.Transport.SnapshotInventoryExchangeCalls}.");
            }
            catch (Exception exception)
            {
                lines.Add($"{prefix}; DiagnosticReadFailure={exception.GetType().Name}.");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<ThreeDeviceSetup> CreateThreeDeviceSetupAsync(bool automaticDiscovery = false)
    {
        ProductionSyncTestHost? original = null;
        ProductionSyncTestHost? middle = null;
        ProductionSyncTestHost? newDevice = null;

        try
        {
            var network = automaticDiscovery ? new PasswordManagerLocal.Common.Tests.Fakes.InProcessDiscoveryNetwork() : null;
            // A and C have no direct discovery route. Their first endpoint must be
            // relayed by B after signed membership reaches both devices.
            network?.BlockPair("192.168.50.1", "192.168.50.3");
            original = await ProductionSyncTestHost.CreateAsync(network?.CreatePort("192.168.50.1"), "192.168.50.1");
            middle = await ProductionSyncTestHost.CreateAsync(network?.CreatePort("192.168.50.2"), "192.168.50.2", DeviceType.AndroidMobile);
            newDevice = await ProductionSyncTestHost.CreateAsync(network?.CreatePort("192.168.50.3"), "192.168.50.3");

            // Keep all transports available, but perform the second enrollment only between the
            // second and third devices. The original device can communicate with the third device
            // later only after ordinary synchronization propagates its membership.
            original.ConnectTo(middle);
            original.ConnectTo(newDevice);
            middle.ConnectTo(original);
            middle.ConnectTo(newDevice);
            newDevice.ConnectTo(original);
            newDevice.ConnectTo(middle);

            var username = $"sync3device{Guid.NewGuid().ToString("N")[..12]}";
            var originalToken = await RunStepAsync("register the original device's profile",
                ct => original.Endpoints.RegisterAsync(original.CreateRegistrationRequest(username), ct));
            await RunStepAsync("write the original device credential",
                ct => AddPasswordAsync(original, originalToken, "Original device credential",
                    Encoding.UTF8.GetBytes("original-device-secret"), ct));
            await RunStepAsync("enable synchronization on the original device", ct => original.EnableSyncAsync(originalToken, ct));

            var middleEnrollmentCode = await RunStepAsync(
                "second device enrollment listener startup",
                ct => middle.Endpoints.StartDeviceEnrollmentAsync(ct));
            await RunStepAsync(
                "first enrollment from original device to second device",
                ct => original.Endpoints.AddDeviceByCodeAsync(originalToken, middleEnrollmentCode.Code, ct));

            var middleToken = await RunStepAsync(
                "second device first login after enrollment",
                ct => middle.Endpoints.LoginAsync(middle.CreateLoginRequest(username), ct));
            await RunStepAsync("enable synchronization on the second device", ct => middle.EnableSyncAsync(middleToken, ct));
            await RunStepAsync("write the second device credential",
                ct => AddPasswordAsync(middle, middleToken, "Second device credential",
                    Encoding.UTF8.GetBytes("second-device-secret"), ct));

            var thirdEnrollmentCode = await RunStepAsync(
                "third device enrollment listener startup",
                ct => newDevice.Endpoints.StartDeviceEnrollmentAsync(ct));
            await RunStepAsync(
                "second enrollment from second device to third device",
                ct => middle.Endpoints.AddDeviceByCodeAsync(middleToken, thirdEnrollmentCode.Code, ct));

            var newDeviceToken = await RunStepAsync(
                "third device first login after enrollment",
                ct => newDevice.Endpoints.LoginAsync(newDevice.CreateLoginRequest(username), ct));
            await RunStepAsync("activate imported synchronization on the third device",
                ct => newDevice.ActivateImportedSynchronizationAsync(ct));

            if (automaticDiscovery)
            {
                foreach (var host in new[] { original, middle, newDevice })
                    await host.Services.GetRequiredService<LocalDiscoveryHostedService>().StartAsync();
            }

            var setup = new ThreeDeviceSetup(
                original,
                middle,
                newDevice,
                username,
                originalToken,
                middleToken,
                newDeviceToken);
            original = null;
            middle = null;
            newDevice = null;
            return setup;
        }
        catch
        {
            if (newDevice is not null)
                await newDevice.DisposeAsync();
            if (middle is not null)
                await middle.DisposeAsync();
            if (original is not null)
                await original.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> HasActiveMembershipForDeviceAsync(
        ProductionSyncTestHost host,
        Guid deviceId)
    {
        using var scope = host.Services.CreateScope();
        var deviceRepository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var userIds = await deviceRepository.ListActiveUserIdsAsync(deviceId);
        return userIds.Count == 1;
    }

    private static async Task<bool> HasPasswordAsync(
        ProductionSyncTestHost host,
        Guid token,
        string name) =>
        (await host.Endpoints.GetSavedPasswordsAsync(token))
        .Passwords.Any(password => password.Name == name);

    private static async Task AddPasswordAsync(
        ProductionSyncTestHost host,
        Guid token,
        string name,
        byte[] secret,
        CancellationToken ct = default)
    {
        await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = name,
            Description = $"Created by {name} in the three-device end-to-end scenario.",
            Color = "#FF123456",
            Password = secret,
            TagIds = []
        }, ct);
    }

    private static async Task AssertPasswordSecretAsync(
        ProductionSyncTestHost host,
        Guid token,
        string name,
        byte[] expectedSecret)
    {
        var data = await host.Endpoints.GetSavedPasswordsAsync(token);
        var password = data.Passwords.Single(item => item.Name == name);
        CollectionAssert.AreEqual(
            expectedSecret,
            await host.Endpoints.GetUnsecurePasswordAsync(token, password.Id));
    }

    private static void AssertDeviceIds(
        IEnumerable<Guid> actualDeviceIds,
        params Guid[] expectedDeviceIds)
    {
        CollectionAssert.AreEquivalent(expectedDeviceIds, actualDeviceIds.ToArray());
    }

    private static async Task AssertOnlineDevicesAsync(
        ProductionSyncTestHost observer,
        Guid observerToken,
        params Guid[] remoteDeviceIds)
    {
        var devices = await observer.Endpoints.GetUserDevicesAsync(observerToken);
        foreach (var remoteDeviceId in remoteDeviceIds)
        {
            Assert.IsTrue(
                devices.Any(device => device.DeviceId == remoteDeviceId && device.IsOnline),
                $"Device {observer.Identity.LocalDeviceId} did not report peer {remoteDeviceId} as online.");
        }
    }

    private async Task<SyncPair> CreateEnrolledPairAsync()
    {
        ProductionSyncTestHost? source = null;
        ProductionSyncTestHost? target = null;
        try
        {
            source = await ProductionSyncTestHost.CreateAsync();
            target = await ProductionSyncTestHost.CreateAsync();
            source.ConnectTo(target);
            target.ConnectTo(source);

            var username = $"synce2e{Guid.NewGuid().ToString("N")[..16]}";
            var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
            await source.EnableSyncAsync(sourceToken);

            var enrollmentCode = await RunStepAsync(
                "target enrollment listener startup for regular-sync test setup",
                ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
            await RunStepAsync(
                "production enrollment setup before regular synchronization",
                ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct));
            var targetToken = await RunStepAsync(
                "first target login before regular synchronization",
                ct => target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));
            await target.EnableSyncAsync(targetToken);

            var pair = new SyncPair(source, target, sourceToken, targetToken);

            var sourceSnapshotInventoryCalls = pair.Source.Transport.SnapshotInventoryExchangeCalls;
            var targetSnapshotInventoryCalls = pair.Target.Transport.SnapshotInventoryExchangeCalls;
            var sourceControlInventoryCalls = pair.Source.Transport.ControlOperationInventoryExchangeCalls;
            var targetControlInventoryCalls = pair.Target.Transport.ControlOperationInventoryExchangeCalls;
            // A production synchronization session deliberately stops after a transient send,
            // validation, or SQLite contention failure and relies on a later discovery/kick to retry
            // the durable queue. A single test kick therefore makes setup timing-dependent. Keep
            // kicking both directions until anti-entropy has run and both durable queues converge.
            await SynchronizeUntilAsync(
                pair,
                "initial post-enrollment regular-sync sessions and queues become idle",
                async () =>
                    pair.Source.Transport.SnapshotInventoryExchangeCalls > sourceSnapshotInventoryCalls &&
                    pair.Target.Transport.SnapshotInventoryExchangeCalls > targetSnapshotInventoryCalls &&
                    pair.Source.Transport.ControlOperationInventoryExchangeCalls > sourceControlInventoryCalls &&
                    pair.Target.Transport.ControlOperationInventoryExchangeCalls > targetControlInventoryCalls &&
                    !await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId) &&
                    !await pair.Target.HasPendingForAsync(pair.Source.Identity.LocalDeviceId));
            await RunStepAsync(
                "stop initial source regular-sync worker",
                ct => pair.Source.SyncTasks.StopAllAsync(ct));
            await RunStepAsync(
                "stop initial target regular-sync worker",
                ct => pair.Target.SyncTasks.StopAllAsync(ct));
            source = null;
            target = null;
            return pair;
        }
        catch
        {
            if (target is not null)
                await target.DisposeAsync();
            if (source is not null)
                await source.DisposeAsync();
            throw;
        }
    }

    private async Task StartSyncAsync(
        ProductionSyncTestHost source,
        ProductionSyncTestHost target,
        string step,
        CancellationToken ct = default)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {step}");
        var started = await source.StartSyncToAsync(target, ct);
        TestContext.WriteLine(
            started
                ? $"[{DateTimeOffset.UtcNow:O}] STARTED: {step}"
                : $"[{DateTimeOffset.UtcNow:O}] PENDING KICK: {step}");

        await source.WaitForSyncIdleAsync(target.Identity.LocalDeviceId, ct);
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] COMPLETE: {step}");
    }

    private async Task SynchronizeOneWayUntilAsync(
        ProductionSyncTestHost source,
        ProductionSyncTestHost target,
        string description,
        Func<Task<bool>> condition)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {description}");
        using var cancellation = new CancellationTokenSource(StepTimeout);
        var sessionCount = 0;

        try
        {
            while (!await condition())
            {
                if (sessionCount >= MaxOneWaySessionsPerStep)
                {
                    var pending = await source.HasPendingForAsync(target.Identity.LocalDeviceId);
                    Assert.Fail(
                        $"Exceeded {MaxOneWaySessionsPerStep} synchronization sessions while waiting for {description}. " +
                        $"Pending={pending}, DeltaSendCalls={source.Transport.DeltaSendCalls}, " +
                        $"SnapshotInventoryCalls={source.Transport.SnapshotInventoryExchangeCalls}, " +
                        $"ControlInventoryCalls={source.Transport.ControlOperationInventoryExchangeCalls}.");
                }

                await StartSyncAsync(
                    source,
                    target,
                    $"{description} (session {sessionCount + 1})",
                    cancellation.Token);
                sessionCount++;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var pending = await source.HasPendingForAsync(target.Identity.LocalDeviceId);
            Assert.Fail(
                $"Timed out waiting for {description}. " +
                $"CompletedSessionCount={sessionCount}, Pending={pending}, " +
                $"DeltaSendCalls={source.Transport.DeltaSendCalls}, " +
                $"SnapshotInventoryCalls={source.Transport.SnapshotInventoryExchangeCalls}, " +
                $"ControlInventoryCalls={source.Transport.ControlOperationInventoryExchangeCalls}.");
        }

        TestContext.WriteLine(
            $"[{DateTimeOffset.UtcNow:O}] COMPLETE: {description}; completedSessionCount={sessionCount}");
    }

    private async Task SynchronizeUntilAsync(
        SyncPair pair,
        string description,
        Func<Task<bool>> condition)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {description}");
        using var cancellation = new CancellationTokenSource(StepTimeout);
        var bidirectionalSessionCount = 0;

        try
        {
            while (!await condition())
            {
                if (bidirectionalSessionCount >= MaxBidirectionalSessionsPerStep)
                {
                    var sourcePending = await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId);
                    var targetPending = await pair.Target.HasPendingForAsync(pair.Source.Identity.LocalDeviceId);
                    Assert.Fail(
                        $"Exceeded {MaxBidirectionalSessionsPerStep} bidirectional synchronization sessions while waiting for {description}. " +
                        $"SourcePending={sourcePending}, TargetPending={targetPending}, " +
                        $"SourceSnapshotInventoryCalls={pair.Source.Transport.SnapshotInventoryExchangeCalls}, " +
                        $"TargetSnapshotInventoryCalls={pair.Target.Transport.SnapshotInventoryExchangeCalls}, " +
                        $"SourceControlInventoryCalls={pair.Source.Transport.ControlOperationInventoryExchangeCalls}, " +
                        $"TargetControlInventoryCalls={pair.Target.Transport.ControlOperationInventoryExchangeCalls}.");
                }

                await Task.WhenAll(
                    StartSyncAsync(
                        pair.Source,
                        pair.Target,
                        $"{description}: source-to-target session {bidirectionalSessionCount + 1}",
                        cancellation.Token),
                    StartSyncAsync(
                        pair.Target,
                        pair.Source,
                        $"{description}: target-to-source session {bidirectionalSessionCount + 1}",
                        cancellation.Token));
                bidirectionalSessionCount++;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var sourcePending = await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId);
            var targetPending = await pair.Target.HasPendingForAsync(pair.Source.Identity.LocalDeviceId);
            Assert.Fail(
                $"Timed out waiting for {description}. " +
                $"CompletedBidirectionalSessionCount={bidirectionalSessionCount}, " +
                $"SourcePending={sourcePending}, TargetPending={targetPending}, " +
                $"SourceSnapshotInventoryCalls={pair.Source.Transport.SnapshotInventoryExchangeCalls}, " +
                $"TargetSnapshotInventoryCalls={pair.Target.Transport.SnapshotInventoryExchangeCalls}, " +
                $"SourceControlInventoryCalls={pair.Source.Transport.ControlOperationInventoryExchangeCalls}, " +
                $"TargetControlInventoryCalls={pair.Target.Transport.ControlOperationInventoryExchangeCalls}.");
        }

        TestContext.WriteLine(
            $"[{DateTimeOffset.UtcNow:O}] COMPLETE: {description}; " +
            $"completedBidirectionalSessionCount={bidirectionalSessionCount}");
    }

    private async Task<T> RunStepAsync<T>(
        string step,
        Func<CancellationToken, Task<T>> operation)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {step}");
        using var cancellation = new CancellationTokenSource();
        var operationTask = Task.Run(() => operation(cancellation.Token));
        var completed = await Task.WhenAny(operationTask, Task.Delay(StepTimeout));
        if (completed == operationTask || operationTask.IsCompleted)
        {
            var result = await operationTask;
            TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] COMPLETE: {step}");
            return result;
        }

        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] TIMEOUT: {step}; requesting cancellation.");
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
            await operationTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
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
            $"Regular synchronization end-to-end step '{step}' did not complete within " +
            $"{StepTimeout.TotalSeconds:0} seconds.");
    }

    private async Task RunStepAsync(
        string step,
        Func<CancellationToken, Task> operation)
    {
        await RunStepAsync<object?>(
            step,
            async ct =>
            {
                await operation(ct);
                return null;
            });
    }

    private sealed class ThreeDeviceSetup : IAsyncDisposable
    {
        public ThreeDeviceSetup(
            ProductionSyncTestHost original,
            ProductionSyncTestHost middle,
            ProductionSyncTestHost newDevice,
            string username,
            Guid originalToken,
            Guid middleToken,
            Guid newDeviceToken)
        {
            Original = original;
            Middle = middle;
            NewDevice = newDevice;
            Username = username;
            OriginalToken = originalToken;
            MiddleToken = middleToken;
            NewDeviceToken = newDeviceToken;
        }

        public ProductionSyncTestHost Original { get; }
        public ProductionSyncTestHost Middle { get; }
        public ProductionSyncTestHost NewDevice { get; }
        public string Username { get; }
        public Guid OriginalToken { get; set; }
        public Guid MiddleToken { get; set; }
        public Guid NewDeviceToken { get; set; }
        public IReadOnlyList<ProductionSyncTestHost> Hosts => [Original, Middle, NewDevice];

        public IReadOnlyList<(ProductionSyncTestHost Source, ProductionSyncTestHost Target)> DirectedPeerPairs =>
        [
            (Original, Middle),
            (Middle, Original),
            (Original, NewDevice),
            (NewDevice, Original),
            (Middle, NewDevice),
            (NewDevice, Middle)
        ];

        public Guid TokenFor(ProductionSyncTestHost host)
        {
            if (ReferenceEquals(host, Original))
                return OriginalToken;
            if (ReferenceEquals(host, Middle))
                return MiddleToken;
            if (ReferenceEquals(host, NewDevice))
                return NewDeviceToken;

            throw new ArgumentException("The host does not belong to this three-device setup.", nameof(host));
        }

        public async ValueTask DisposeAsync()
        {
            Original.Transport.Disconnect();
            Middle.Transport.Disconnect();
            NewDevice.Transport.Disconnect();
            await NewDevice.DisposeAsync();
            await Middle.DisposeAsync();
            await Original.DisposeAsync();
        }
    }

    private sealed class SyncPair : IAsyncDisposable
    {
        public SyncPair(
            ProductionSyncTestHost source,
            ProductionSyncTestHost target,
            Guid sourceToken,
            Guid targetToken)
        {
            Source = source;
            Target = target;
            SourceToken = sourceToken;
            TargetToken = targetToken;
        }

        public ProductionSyncTestHost Source { get; }
        public ProductionSyncTestHost Target { get; }
        public Guid SourceToken { get; }
        public Guid TargetToken { get; }

        public async ValueTask DisposeAsync()
        {
            Source.Transport.Disconnect();
            Target.Transport.Disconnect();
            await Target.DisposeAsync();
            await Source.DisposeAsync();
        }
    }
}
