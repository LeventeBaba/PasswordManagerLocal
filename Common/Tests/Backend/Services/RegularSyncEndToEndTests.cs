using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class RegularSyncEndToEndTests
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const int MaxOneWaySessionsPerPhase = 12;
    private const int MaxBidirectionalSessionsPerPhase = 12;
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

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

        var enrollmentCode = await RunPhaseAsync(
            "target enrollment listener startup before realistic post-enrollment synchronization",
            ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
        await RunPhaseAsync(
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
        var sourceReloginToken = await RunPhaseAsync(
            "original device login after enrollment and post-enrollment synchronization",
            ct => source.Endpoints.LoginAsync(source.CreateLoginRequest(username), ct));
        var targetFirstLoginToken = await RunPhaseAsync(
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
            "Test data arrangement unexpectedly started regular synchronization before the explicit sync phase.");

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

            var enrollmentCode = await RunPhaseAsync(
                "target enrollment listener startup for regular-sync test setup",
                ct => target.Endpoints.StartDeviceEnrollmentAsync(ct));
            await RunPhaseAsync(
                "production enrollment setup before regular synchronization",
                ct => source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollmentCode.Code, ct));
            var targetToken = await RunPhaseAsync(
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
            await RunPhaseAsync(
                "stop initial source regular-sync worker",
                ct => pair.Source.SyncTasks.StopAllAsync(ct));
            await RunPhaseAsync(
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
        string phase,
        CancellationToken ct = default)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {phase}");
        var started = await source.StartSyncToAsync(target, ct);
        TestContext.WriteLine(
            started
                ? $"[{DateTimeOffset.UtcNow:O}] STARTED: {phase}"
                : $"[{DateTimeOffset.UtcNow:O}] PENDING KICK: {phase}");

        await source.WaitForSyncIdleAsync(target.Identity.LocalDeviceId, ct);
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] COMPLETE: {phase}");
    }

    private async Task SynchronizeOneWayUntilAsync(
        ProductionSyncTestHost source,
        ProductionSyncTestHost target,
        string description,
        Func<Task<bool>> condition)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {description}");
        using var cancellation = new CancellationTokenSource(PhaseTimeout);
        var sessionCount = 0;

        try
        {
            while (!await condition())
            {
                if (sessionCount >= MaxOneWaySessionsPerPhase)
                {
                    var pending = await source.HasPendingForAsync(target.Identity.LocalDeviceId);
                    Assert.Fail(
                        $"Exceeded {MaxOneWaySessionsPerPhase} synchronization sessions while waiting for {description}. " +
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
        using var cancellation = new CancellationTokenSource(PhaseTimeout);
        var bidirectionalSessionCount = 0;

        try
        {
            while (!await condition())
            {
                if (bidirectionalSessionCount >= MaxBidirectionalSessionsPerPhase)
                {
                    var sourcePending = await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId);
                    var targetPending = await pair.Target.HasPendingForAsync(pair.Source.Identity.LocalDeviceId);
                    Assert.Fail(
                        $"Exceeded {MaxBidirectionalSessionsPerPhase} bidirectional synchronization sessions while waiting for {description}. " +
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

    private async Task<T> RunPhaseAsync<T>(
        string phase,
        Func<CancellationToken, Task<T>> operation)
    {
        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] START: {phase}");
        using var cancellation = new CancellationTokenSource();
        var operationTask = Task.Run(() => operation(cancellation.Token));
        var completed = await Task.WhenAny(operationTask, Task.Delay(PhaseTimeout));
        if (completed == operationTask || operationTask.IsCompleted)
        {
            var result = await operationTask;
            TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] COMPLETE: {phase}");
            return result;
        }

        TestContext.WriteLine($"[{DateTimeOffset.UtcNow:O}] TIMEOUT: {phase}; requesting cancellation.");
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
            $"Regular synchronization end-to-end phase '{phase}' did not complete within " +
            $"{PhaseTimeout.TotalSeconds:0} seconds.");
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
