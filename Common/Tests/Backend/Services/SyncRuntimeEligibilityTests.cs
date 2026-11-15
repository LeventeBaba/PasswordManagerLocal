using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SyncRuntimeEligibilityTests
{
    [TestMethod]
    public async Task EmptyVault_AndLocalOnlyProfiles_AreIntentionallyIdle()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual(SyncRuntimeEligibilityReason.NoLocalProfiles, (await fixture.EvaluateAsync()).Reason);
        await fixture.AddProfileAsync(withRemote: false);
        Assert.AreEqual(SyncRuntimeEligibilityReason.NoRemoteDevices, (await fixture.EvaluateAsync()).Reason);
        Assert.IsFalse((await fixture.EvaluateAsync()).HasHistoricalControlEvidence, "Local-only genesis does not justify a passive listener.");
        await fixture.AddProfileAsync(withRemote: false);
        Assert.AreEqual(SyncRuntimeEligibilityReason.NoRemoteDevices, (await fixture.EvaluateAsync()).Reason);
    }

    [TestMethod]
    [DataRow("eligible", true)]
    [DataRow("offline", true)]
    [DataRow("local-disabled", false)]
    [DataRow("remote-disabled", false)]
    [DataRow("blocked", false)]
    [DataRow("untrusted", false)]
    [DataRow("removed", false)]
    [DataRow("remote-unauthorized", false)]
    [DataRow("local-unauthorized", false)]
    [DataRow("wrong-origin", false)]
    [DataRow("wrong-signing-key", false)]
    [DataRow("wrong-certificate", false)]
    [DataRow("authority-conflict", false)]
    [DataRow("removal-cutoff", false)]
    [DataRow("deleted-account", false)]
    public async Task EligibilityMatrix_UsesPersistedFlagsAndExactCurrentAuthority(string scenario, bool expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (user, device) = await fixture.AddProfileAsync();
        switch (scenario)
        {
            case "local-disabled":
                var local = (await fixture.Db.LocalUserDevices.GetAsync(user.UId))!;
                local.IsSyncOn = false; fixture.Db.LocalUserDevices.Update(local); break;
            case "remote-disabled":
            case "removed":
                var remote = (await fixture.Db.UserDevices.GetAsync(user.UId, device!.Id))!;
                remote.IsSyncOn = scenario != "remote-disabled";
                remote.IsDeleted = scenario == "removed";
                fixture.Db.UserDevices.Update(remote); break;
            case "blocked":
            case "untrusted":
            case "offline":
                device = (await fixture.Db.Devices.GetByIdAsync(device!.Id))!;
                device.IsBlocked = scenario == "blocked";
                device.IsTrusted = scenario != "untrusted";
                device.LastSeen = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                fixture.Db.Devices.Update(device); break;
            case "remote-unauthorized":
            case "local-unauthorized":
                var member = (await fixture.Db.UserMembershipAuthorizations.ListActiveForDeviceAsync(user.UId,
                    scenario == "local-unauthorized" ? fixture.Identity.LocalDeviceId : device!.Id)).Single();
                member.IsActive = false; member.EndedMembershipEpoch = 3;
                fixture.Db.UserMembershipAuthorizations.Update(member); break;
            case "wrong-origin":
                fixture.Identity.OriginInstanceId = Guid.NewGuid(); break;
            case "wrong-signing-key":
            case "wrong-certificate":
                var authorization = (await fixture.Db.UserMembershipAuthorizations.ListActiveForDeviceAsync(user.UId, device!.Id)).Single();
                if (scenario == "wrong-signing-key") authorization.SignPublicKeyHash = new byte[32];
                else authorization.TlsCertFingerprint = new string('C', 64);
                fixture.Db.UserMembershipAuthorizations.Update(authorization); break;
            case "removal-cutoff":
                var removed = (await fixture.Db.UserMembershipAuthorizations.ListActiveForDeviceAsync(user.UId, device!.Id)).Single();
                await fixture.Db.UserOriginRemovalCutoffs.AddAsync(new UserOriginRemovalCutoff { CutoffId = Guid.NewGuid(),
                    UserId = user.UId, DeviceId = device.Id, OriginInstanceId = removed.OriginInstanceId, UserKeyEpoch = 1 }); break;
            case "authority-conflict":
                await fixture.Db.UserControlStates.AddAsync(new UserControlState { UserId = user.UId, HasConflict = true }); break;
            case "deleted-account":
                await fixture.Db.DeletedUserBarriers.AddAsync(new DeletedUserBarrier { UserId = user.UId }); break;
        }
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        var result = await fixture.EvaluateAsync();
        Assert.AreEqual(expected, result.IsEligible, scenario);
        Assert.AreEqual(expected ? SyncRuntimeEligibilityReason.Eligible : scenario == "local-disabled"
            ? SyncRuntimeEligibilityReason.LocalSynchronizationDisabled : SyncRuntimeEligibilityReason.NoEligibleRemoteDevices, result.Reason);
        Assert.AreEqual(scenario is not ("blocked" or "untrusted" or "wrong-signing-key" or "wrong-certificate"),
            result.HasHistoricalControlEvidence);
        Assert.IsFalse(fixture.Identity.IsSyncOn, "Eligibility must not use or write the derived runtime flag.");
        Assert.AreEqual(0, fixture.Identity.SetSyncOnCalls);
    }

    [TestMethod]
    public async Task OneEligibleRemoteInOneProfile_IsSufficient_AndTheLocalDeviceNeverCounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (firstUser, firstDevice) = await fixture.AddProfileAsync();
        firstDevice!.IsBlocked = true; fixture.Db.Devices.Update(firstDevice);
        var (secondUser, secondDevice) = await fixture.AddProfileAsync();
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsTrue((await fixture.EvaluateAsync()).IsEligible);
        var secondLink = (await fixture.Db.UserDevices.GetAsync(secondUser.UId, secondDevice!.Id))!;
        secondLink.IsSyncOn = false; fixture.Db.UserDevices.Update(secondLink);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsFalse((await fixture.EvaluateAsync()).IsEligible);
        firstDevice.IsBlocked = false; fixture.Db.Devices.Update(firstDevice);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsTrue((await fixture.EvaluateAsync()).IsEligible);
        fixture.Db.UserDevices.Delete((await fixture.Db.UserDevices.GetAsync(firstUser.UId, firstDevice.Id))!);
        var localDevice = new Device { Id = fixture.Identity.LocalDeviceId, PublicKey = [1], SignPublicKey = [2],
            TlsCertFingerprint = fixture.Identity.FingerprintHex, IsTrusted = true };
        await fixture.Db.Devices.AddAsync(localDevice);
        await fixture.Db.UserDevices.AddAsync(new UserDevice { UserId = firstUser.UId, DeviceId = localDevice.Id, IsSyncOn = true });
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsFalse((await fixture.EvaluateAsync()).IsEligible);
    }

    [TestMethod]
    public async Task RemoteTransitionsAndRestart_ReReadCommittedStateWithoutPresenceOrVaultDecryption()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (user, device) = await fixture.AddProfileAsync();
        Assert.IsTrue((await fixture.EvaluateAsync()).IsEligible);
        var link = (await fixture.Db.UserDevices.GetAsync(user.UId, device!.Id))!;
        link.IsSyncOn = false; fixture.Db.UserDevices.Update(link);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsFalse((await fixture.EvaluateAsync()).IsEligible);
        link.IsSyncOn = true; fixture.Db.UserDevices.Update(link);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        using var restartedSession = fixture.Db.OpenSession();
        Assert.IsTrue((await new SyncRuntimeEligibilityRepository(restartedSession, fixture.Identity).EvaluateAsync()).IsEligible);
        device = (await fixture.Db.Devices.GetByIdAsync(device.Id))!;
        device.IsBlocked = true; fixture.Db.Devices.Update(device);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsFalse((await fixture.EvaluateAsync()).IsEligible);
        device.IsBlocked = false; fixture.Db.Devices.Update(device);
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsTrue((await fixture.EvaluateAsync()).IsEligible);
    }

    [TestMethod]
    public async Task CorruptQualifyingRelationship_FailsClosed_AndInvalidIdentityCannotEnableNetworking()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (user, _) = await fixture.AddProfileAsync();
        using (var command = await fixture.Db.UnitOfWork.CreateCommandAsync("""
            UPDATE "UserDevices" SET "IntegrityHash" = X'00' WHERE "UserId" = $user
            """))
        {
            command.Parameters.AddWithValue("$user", user.UId);
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() => fixture.EvaluateAsync());
        fixture.Identity.IsInitialized = false;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.EvaluateAsync());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public SqliteRepositoryTestDatabase Db { get; }
        public FakeDeviceIdentityService Identity { get; }
        private Fixture(SqliteRepositoryTestDatabase db, FakeDeviceIdentityService identity) { Db = db; Identity = identity; }
        public static async Task<Fixture> CreateAsync(IRevocationDeliveryActivity? activity = null)
        {
            var db = await SqliteRepositoryTestDatabase.CreateAsync(activity: activity);
            var local = new LocalDeviceIdentity();
            await db.DeviceIdentity.Create(local); await db.UnitOfWork.SaveChangesAsync();
            return new(db, new FakeDeviceIdentityService { LocalDeviceId = local.Id, OriginInstanceId = local.OriginInstanceId,
                IsSyncOn = false, SignPublicKey = [2], AgreementPublicKey = [1], FingerprintHex = new string('A', 64) });
        }
        public Task<SyncRuntimeEligibilityResult> EvaluateAsync() => new SyncRuntimeEligibilityRepository(Db.UnitOfWork, Identity).EvaluateAsync();
        public async Task<(User User, Device? Device)> AddProfileAsync(bool withRemote = true)
        {
            var user = new User { UsernameHash = Guid.NewGuid().ToByteArray(), UsernameSalt = [2], PasswordSalt = [3],
                EncryptedPayload = [4], EncryptedGeneralUserDataPayload = [5], EncryptedUserPasswordsDataPayload = [6],
                EncryptedUserDevicesDataPayload = [7], KeyEpoch = 1, MembershipEpoch = 2 };
            await Db.Users.AddAsync(user);
            await Db.LocalUserDevices.AddAsync(new LocalUserDevice { UserId = user.UId, LocalDeviceIdentityId = Identity.LocalDeviceId, IsSyncOn = true });
            await Db.UserMembershipAuthorizations.AddAsync(Member(user.UId, Identity.LocalDeviceId, Identity.OriginInstanceId,
                Identity.SignPublicKey, Identity.AgreementPublicKey, Identity.FingerprintHex));
            Device? remote = null;
            if (withRemote)
            {
                remote = new Device
                {
                    IsTrusted = true,
                    PublicKey = Guid.NewGuid().ToByteArray(),
                    SignPublicKey = Guid.NewGuid().ToByteArray(),
                    TlsCertFingerprint = Convert.ToHexString(Hashing.SHA256Hash(Guid.NewGuid().ToByteArray()))
                };
                await Db.Devices.AddAsync(remote);
                await Db.UserDevices.AddAsync(new UserDevice { UserId = user.UId, DeviceId = remote.Id, IsSyncOn = true });
                await Db.UserMembershipAuthorizations.AddAsync(Member(user.UId, remote.Id, Guid.NewGuid(), remote.SignPublicKey, remote.PublicKey, remote.TlsCertFingerprint));
            }
            await Db.UnitOfWork.SaveChangesAsync();
            return (user, remote);
        }
        private static UserMembershipAuthorization Member(Guid userId, Guid deviceId, Guid origin, byte[] signKey, byte[] agreementKey, string fingerprint) => new()
        {
            AuthorizationId = Guid.NewGuid(), UserId = userId, DeviceId = deviceId, OriginInstanceId = origin,
            SignPublicKey = signKey, SignPublicKeyHash = Hashing.SHA256Hash(signKey), AgreementPublicKeyHash = Hashing.SHA256Hash(agreementKey),
            TlsCertFingerprint = fingerprint, IsActive = true, StartedMembershipEpoch = 1, MinimumKeyEpoch = 1
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
