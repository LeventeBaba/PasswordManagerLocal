using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class EnrollmentSyncHardeningTests
{
    [TestMethod]
    [Timeout(60000)]
    public async Task CommittedEditWithHeldStaleCache_DeviceEditAndRestartPreservePassword()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        const string username = "held_refresh_regression";
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        using var before = host.Services.CreateScope();
        using var stale = await before.ServiceProvider.GetRequiredService<IUserDataReaderService>()
            .GetLoadAndVerifyUserDataBundleAsync(token);
        var oldUser = await before.ServiceProvider.GetRequiredService<IUserRepository>()
            .GetByIdAsNoTrackingAsync(stale.UserData.UId);

        // Commit a newer canonical password while keeping the old decrypted generation alive.
        // This deterministic boundary models a merge whose session-refresh continuation is held.
        await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Committed while refresh held", Password = Encoding.UTF8.GetBytes("RetainedSecret123!")
        });
        host.Services.GetRequiredService<IDataCachingService>().SetUserDataBundle(token, stale);
        await host.Endpoints.SetLocalDeviceNameAsync(token, "Intentional PC name");

        // Release the OLD refresh after both newer commits. It must reload the latest generation.
        using (var refresh = host.Services.CreateScope())
            await refresh.ServiceProvider.GetRequiredService<IAuthSessionService>().RefreshSyncedUserSessionsAsync(oldUser!);
        using (var verify = host.Services.CreateScope())
        {
            using var disk = await verify.ServiceProvider.GetRequiredService<IUserDataReaderService>()
                .GetLoadAndVerifyUserDataBundleAsync(token);
            Assert.AreEqual("Committed while refresh held", disk.UserPasswordsData.Passwords.Single().Name);
            Assert.AreEqual("Intentional PC name", disk.UserDevicesData.Devices.Single().Name);
            var cache = host.Services.GetRequiredService<IDataCachingService>();
            Assert.IsTrue(cache.TryGetUserDataBundle(token, out var cached));
            using (cached)
                CollectionAssert.AreEqual(disk.CanonicalGeneration!, cached!.CanonicalGeneration!);
        }

        await using var restarted = await host.RestartAsync();
        var login = await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username));
        Assert.AreEqual("Committed while refresh held", (await restarted.Endpoints.GetSavedPasswordsAsync(login)).Passwords.Single().Name);
        Assert.AreEqual("Intentional PC name", (await restarted.Endpoints.GetUserDevicesAsync(login)).Single().Name);
    }

    [TestMethod]
    public async Task StalePartialWrite_IsRejectedBeforeCanonicalCiphertextsChange()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("stale_write_guard"));
        using var read = host.Services.CreateScope();
        using var stale = await read.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token);
        await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Keep", Password = Encoding.UTF8.GetBytes("KeepThisSecret123!")
        });
        using var write = host.Services.CreateScope();
        var writer = write.ServiceProvider.GetRequiredService<IUserDataWriterService>();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.UpdateUserDataBundleAsync(stale, token, UserDataBlobKind.Devices));
        // Even a legacy bundle without provenance must fail the actual retained-child check.
        stale.CanonicalGeneration = null;
        await Assert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() => writer.UpdateUserDataBundleAsync(stale, token, UserDataBlobKind.Devices));
        using var verify = host.Services.CreateScope();
        using var disk = await verify.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token);
        Assert.AreEqual("Keep", disk.UserPasswordsData.Passwords.Single().Name);
    }

    [TestMethod]
    public async Task IndependentWorkersStartedUnderAccountLock_WaitUntilParentReleases()
    {
        var lifecycle = new UserLifecycleCoordinator();
        var userId = Guid.NewGuid();
        var checkedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        Task? worker = null;
        await lifecycle.ExecuteAsync(userId, async ct =>
        {
            worker = IndependentBackgroundWork.Run(async () =>
            {
                var pending = lifecycle.ExecuteAsync(userId, _ =>
                {
                    entered = true;
                    return Task.CompletedTask;
                });
                checkedGate.SetResult(pending.IsCompleted);
                await pending;
            });
            Assert.IsFalse(await checkedGate.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(entered);
            await lifecycle.ExecuteAsync(userId, _ => Task.CompletedTask, ct); // awaited nesting still works
        });
        await worker!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(entered);
    }

    [TestMethod]
    public void LoginTimestampDoesNotOverwriteConcurrentExplicitName_InEitherMergeOrder()
    {
        var id = Guid.NewGuid();
        var origin = Guid.NewGuid();
        SyncVersionStamp Stamp(long time) => new()
        {
            PhysicalTimeUnixMilliseconds = time, OriginDeviceId = id, OriginInstanceId = origin
        };
        UserDeviceData Device(string name, long version, long nameVersion) => new()
        {
            Id = id, Name = name, LinkedAt = DateTimeOffset.FromUnixTimeMilliseconds(1),
            LastUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(version), Version = Stamp(version), NameVersion = Stamp(nameVersion)
        };
        using var renamed = new UserDevicesData { Devices = [Device("Chosen name", 20, 20)] };
        using var loggedIn = new UserDevicesData { Devices = [Device("Old name", 30, 10)] };
        var merge = new UserDevicesDataMergeService();
        Assert.IsTrue(merge.Merge(loggedIn, renamed));
        Assert.AreEqual("Chosen name", loggedIn.Devices.Single().Name);
        Assert.IsTrue(merge.Merge(renamed, loggedIn));
        CollectionAssert.AreEqual(renamed.CalculateIntegrityHash(), loggedIn.CalculateIntegrityHash());
        Assert.IsFalse(merge.Merge(renamed, loggedIn));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ReplacedAdvertisedSnapshot_RetriesWithoutStrikes_WhileMalformedInputStillBlocks()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var peer = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(peer);
        peer.ConnectTo(source);
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("snapshot_churn"));
        await source.EnableSyncAsync(token);
        var code = await peer.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        await peer.ActivateImportedSynchronizationAsync();
        source.EndpointsCache.Clear();
        peer.EndpointsCache.Clear();
        await source.SyncTasks.StopAllAsync();
        await peer.SyncTasks.StopAllAsync();
        using (var gap = source.Services.CreateScope())
        {
            var reader = gap.ServiceProvider.GetRequiredService<IUserDataReaderService>();
            using var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
            bundle.UserDevicesData.Devices.RemoveAll(x => x.Id == peer.Identity.LocalDeviceId);
            await gap.ServiceProvider.GetRequiredService<IUserDataWriterService>().UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices);
        }
        byte[] beforeList;
        using (var scope = source.Services.CreateScope())
        {
            using var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token);
            beforeList = bundle.CanonicalGeneration!.ToArray();
        }
        Assert.IsTrue((await source.Endpoints.GetUserDevicesAsync(token)).Any(x => x.DeviceId == peer.Identity.LocalDeviceId));
        using (var scope = source.Services.CreateScope())
        {
            using var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token);
            CollectionAssert.AreEqual(beforeList, bundle.CanonicalGeneration!);
            Assert.IsFalse(bundle.UserDevicesData.Devices.Any(x => x.Id == peer.Identity.LocalDeviceId));
        }
        await source.PublishCurrentUserSnapshotAsync();
        UserSnapshotRequest oldRequest;
        using (var scope = source.Services.CreateScope())
        {
            var inventory = await scope.ServiceProvider.GetRequiredService<IUserSnapshotAntiEntropyService>().BuildInventoryAsync(peer.Identity.LocalDeviceId);
            var user = inventory.Users.Single();
            var revision = user.Revisions.Single(x => x.OriginDeviceId == source.Identity.LocalDeviceId.ToString("N"));
            oldRequest = new UserSnapshotRequest
            {
                UserId = user.UserId, OriginDeviceId = revision.OriginDeviceId, OriginInstanceId = revision.OriginInstanceId,
                OriginRevision = revision.HighestStoredRevision, UserKeyEpoch = revision.UserKeyEpoch,
                MembershipEpoch = revision.RetainedMembershipEpoch, ExpectedSnapshotHash = revision.HighestStoredSnapshotHash
            };
        }
        await source.Endpoints.SetLocalDeviceNameAsync(token, "New publication");
        await source.PublishCurrentUserSnapshotAsync();
        var context = new PeerConnectionContext
        {
            ClientCertificateFingerprint = peer.Identity.FingerprintHex, RemoteIpAddress = "192.168.1.2",
            SyncHelloAccepted = true, RemoteDatabaseVersion = DatabaseConstants.CurrentDbVersion,
            RemoteProtocolVersion = SyncConstants.SyncProtocolVersion
        };
        using (var inventoryScope = peer.Services.CreateScope())
        {
            var staleInventory = await inventoryScope.ServiceProvider.GetRequiredService<IUserSnapshotAntiEntropyService>()
                .BuildInventoryAsync(source.Identity.LocalDeviceId);
            staleInventory.Users.Single().MembershipEpoch--;
            var stale = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() =>
                source.ProtocolHandler.ExchangeUserSnapshotInventoryAsync(staleInventory, context, CancellationToken.None));
            Assert.AreEqual(SyncProtocolStatusCode.Unavailable, stale.StatusCode);
        }
        var batch = new UserSnapshotRequestBatch();
        batch.Requests.Add(oldRequest);
        for (var i = 0; i < SyncConstants.MaxInvalidIncomingSyncAttempts + 2; i++)
        {
            var failure = await Assert.ThrowsExactlyAsync<SyncProtocolException>(() => source.ProtocolHandler.RequestUserSnapshotsAsync(batch, context, CancellationToken.None));
            Assert.AreEqual(SyncProtocolStatusCode.Unavailable, failure.StatusCode);
        }
        using (var scope = source.Services.CreateScope())
        {
            var device = await scope.ServiceProvider.GetRequiredService<IDeviceRepository>().GetByIdAsNoTrackingAsync(peer.Identity.LocalDeviceId);
            Assert.AreEqual(0, device!.InvalidSyncAttemptCount);
            Assert.IsFalse(device.IsBlocked);
        }
        oldRequest.ExpectedSnapshotHash = ByteString.Empty; // provably malformed, not churn
        for (var i = 0; i < SyncConstants.MaxInvalidIncomingSyncAttempts; i++)
        {
            await Assert.ThrowsExactlyAsync<SyncProtocolException>(() => source.ProtocolHandler.RequestUserSnapshotsAsync(batch, context, CancellationToken.None));
            if (i == 0) await source.ProtocolHandler.PushDeltaAsync(EmptyChunks(), context, CancellationToken.None);
        }
        using (var scope = source.Services.CreateScope())
        {
            var device = await scope.ServiceProvider.GetRequiredService<IDeviceRepository>().GetByIdAsNoTrackingAsync(peer.Identity.LocalDeviceId);
            Assert.AreEqual(SyncConstants.MaxInvalidIncomingSyncAttempts, device!.InvalidSyncAttemptCount);
            Assert.IsTrue(device.IsBlocked);
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task DamagedCanonicalProjection_UsesSignedEvidenceBeforePasswordLogin()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        const string username = "recover_projection";
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Recovery evidence", Password = Encoding.UTF8.GetBytes("RecoverySecret123!")
        });
        await host.PublishCurrentUserSnapshotAsync();
        await host.Endpoints.LogoutAsync(token);
        using (var corrupt = host.Services.CreateScope())
        {
            var users = corrupt.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = (await users.GetByIdAsync((await users.ListUserIdsAsync()).Single()))!;
            user.EncryptedUserDevicesDataPayload = [1, 2, 3];
            user.UsernameHash = Enumerable.Repeat((byte)0x70, 32).ToArray();
            user.UsernameSalt = Enumerable.Repeat((byte)0x71, 32).ToArray();
            user.GenerateIntegrityHash();
            users.Update(user);
            await corrupt.ServiceProvider.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.Persistence.IUnitOfWork>().SaveChangesAsync();
        }
        var login = await host.Endpoints.LoginAsync(host.CreateLoginRequest(username));
        Assert.AreEqual("Recovery evidence", (await host.Endpoints.GetSavedPasswordsAsync(login)).Passwords.Single().Name);
    }

    [TestMethod]
    public async Task CacheOwnsCopy_AfterCallerDisposalAndConcurrentReaders()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("cache_ownership"));
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        using (var scope = host.Services.CreateScope())
        {
            using var working = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token);
            cache.SetUserDataBundle(token, working);
        }
        Assert.IsTrue(cache.TryGetUserDataBundle(token, out var first));
        Assert.IsTrue(cache.TryGetUserDataBundle(token, out var second));
        using (first)
        using (second)
        {
            first!.GeneralUserData.Username = "Changed copy";
            Assert.AreEqual("cache_ownership", second!.GeneralUserData.Username);
            Assert.AreNotEqual(Guid.Empty, second.UserData.UId);
        }
    }

    private static async IAsyncEnumerable<DeltaChunk> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }
}
