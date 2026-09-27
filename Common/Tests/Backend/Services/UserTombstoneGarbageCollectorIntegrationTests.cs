using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserTombstoneGarbageCollectorIntegrationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public async Task CollectAsync_StableLocalTombstones_RemovesEveryEncryptedTypeAndIsIdempotent()
    {
        using var host = new BackendTestHost();
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var passwords = services.GetRequiredService<IUserPasswordsService>();
        var sessions = services.GetRequiredService<IUserSessionService>();
        var users = services.GetRequiredService<IUserRepository>();
        var reader = services.GetRequiredService<IUserDataReaderService>();
        var writer = services.GetRequiredService<IUserDataWriterService>();
        var identity = services.GetRequiredService<IDeviceIdentityService>();
        var versionClock = services.GetRequiredService<ISyncVersionClockService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("causal-gc-all-types"));
        await passwords.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Live",
            Password = Encoding.UTF8.GetBytes("still-live")
        });

        var userId = sessions.GetUidFromToken(token);
        using var key = sessions.GetEncryptionKeyFromToken(token);
        var user = await users.GetByIdWithRelationsAsync(userId)
            ?? throw new AssertFailedException("The registered user was not found.");

        SyncVersionStamp liveVersion;
        using (var bundle = await reader.GetAndVerifyUserDataBundleAsync(user, key))
        {
            liveVersion = bundle.UserPasswordsData.Passwords.Single().Version with { };
            bundle.UserPasswordsData.DeletedPasswords.Add(new DeletedPasswordData
            {
                Id = Guid.NewGuid(),
                DeletedAt = DateTime.UnixEpoch,
                Version = versionClock.Next()
            });
            bundle.UserPasswordsData.DeletedTags.Add(new DeletedPasswordTagData
            {
                Id = Guid.NewGuid(),
                DeletedAt = DateTime.UnixEpoch,
                Version = versionClock.Next()
            });
            bundle.UserPasswordsData.DeletedCustomColors.Add(new DeletedCustomUserColorData
            {
                Id = Guid.NewGuid(),
                DeletedAt = DateTime.UnixEpoch,
                Version = versionClock.Next()
            });
            bundle.UserDevicesData.DeletedDevices.Add(new DeletedUserDeviceData
            {
                Id = Guid.NewGuid(),
                DeletedAt = DateTimeOffset.UnixEpoch,
                Version = versionClock.Next()
            });

            await writer.UpdateUserDataBundleAsync(
                bundle,
                user,
                key,
                UserDataBlobKind.Passwords | UserDataBlobKind.Devices,
                enqueueSync: false);
        }

        var publisher = CreateRealPublisher(services);
        var logicalTimestamps = (
            user.LastModifiedAt,
            user.UserDataLastModifiedAt,
            user.GeneralUserDataLastModifiedAt,
            user.UserPasswordsDataLastModifiedAt,
            user.UserDevicesDataLastModifiedAt);
        var anchor = await publisher.GetOrCreateAsync(user);
        MSTestAssert.AreEqual(1L, anchor.OriginRevision);

        var collector = CreateCollector(services, publisher);
        var first = await collector.CollectAsync(userId, key);

        MSTestAssert.IsTrue(first.Changed);
        MSTestAssert.AreEqual(4, first.ExaminedCount);
        MSTestAssert.AreEqual(4, first.RemovedCount);
        MSTestAssert.AreEqual(0, first.RetainedCount);
        MSTestAssert.IsNull(first.GlobalBlockReason);

        var refreshed = await users.GetByIdWithRelationsAsync(userId)
            ?? throw new AssertFailedException("The compacted user was not found.");
        MSTestAssert.AreEqual(logicalTimestamps.LastModifiedAt, refreshed.LastModifiedAt);
        MSTestAssert.AreEqual(logicalTimestamps.UserDataLastModifiedAt, refreshed.UserDataLastModifiedAt);
        MSTestAssert.AreEqual(logicalTimestamps.GeneralUserDataLastModifiedAt, refreshed.GeneralUserDataLastModifiedAt);
        MSTestAssert.AreEqual(logicalTimestamps.UserPasswordsDataLastModifiedAt, refreshed.UserPasswordsDataLastModifiedAt);
        MSTestAssert.AreEqual(logicalTimestamps.UserDevicesDataLastModifiedAt, refreshed.UserDevicesDataLastModifiedAt);
        using (var compacted = await reader.GetAndVerifyUserDataBundleAsync(refreshed, key))
        {
            MSTestAssert.IsEmpty(compacted.UserPasswordsData.DeletedPasswords);
            MSTestAssert.IsEmpty(compacted.UserPasswordsData.DeletedTags);
            MSTestAssert.IsEmpty(compacted.UserPasswordsData.DeletedCustomColors);
            MSTestAssert.IsEmpty(compacted.UserDevicesData.DeletedDevices);
            MSTestAssert.AreEqual(liveVersion, compacted.UserPasswordsData.Passwords.Single().Version);
        }

        var latest = await publisher.GetLatestAsync(userId, refreshed.KeyEpoch);
        MSTestAssert.IsNotNull(latest);
        MSTestAssert.AreEqual(2L, latest.OriginRevision);

        var second = await collector.CollectAsync(userId, key);
        MSTestAssert.IsFalse(second.Changed);
        MSTestAssert.AreEqual(0, second.ExaminedCount);
        MSTestAssert.AreEqual(0, second.RemovedCount);

        var unchangedLatest = await publisher.GetLatestAsync(userId, refreshed.KeyEpoch);
        MSTestAssert.IsNotNull(unchangedLatest);
        MSTestAssert.AreEqual(2L, unchangedLatest.OriginRevision);
    }

    private static IUserSnapshotPublisherService CreateRealPublisher(IServiceProvider services) =>
        new UserSnapshotPublisherService(
            services.GetRequiredService<IUserSyncSnapshotRepository>(),
            services.GetRequiredService<IUserSyncStateRepository>(),
            services.GetRequiredService<IUserRevisionKnowledgeRepository>(),
            services.GetRequiredService<IDeviceIdentityService>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IUserLifecycleCoordinator>(),
            services.GetRequiredService<IDeletedUserBarrierRepository>());

    private static IUserTombstoneGarbageCollector CreateCollector(
        IServiceProvider services,
        IUserSnapshotPublisherService publisher) =>
        new UserTombstoneGarbageCollector(
            services.GetRequiredService<IUserRepository>(),
            services.GetRequiredService<IDeletedUserBarrierRepository>(),
            services.GetRequiredService<IUserMembershipAuthorizationRepository>(),
            services.GetRequiredService<IUserOriginRemovalCutoffRepository>(),
            services.GetRequiredService<IUserRevisionKnowledgeRepository>(),
            services.GetRequiredService<IUserSyncSnapshotRepository>(),
            services.GetRequiredService<IUserMembershipAuthorizationService>(),
            services,
            services.GetRequiredService<IUserDataReaderService>(),
            services.GetRequiredService<IUserDataWriterService>(),
            publisher,
            services.GetRequiredService<ISyncQueueWriterService>(),
            services.GetRequiredService<IPendingSyncActivationService>(),
            services.GetRequiredService<IUserLifecycleCoordinator>(),
            services.GetRequiredService<IInteractiveSessionStateService>(),
            services.GetRequiredService<IUnitOfWork>());
}
