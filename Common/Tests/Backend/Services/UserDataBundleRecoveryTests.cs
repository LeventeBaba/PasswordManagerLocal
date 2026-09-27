using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataBundleRecoveryTests
{
    private static readonly Guid DeviceA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid DeviceB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid DeviceC = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid InstanceA = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid InstanceB = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid InstanceC = Guid.Parse("33000000-0000-0000-0000-000000000003");

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Recovery")]
    public async Task TryReconstructCanonicalAsync_CandidatePermutations_ProduceSameLogicalResultAndPreserveVersions()
    {
        using var material = new RecoveryTestMaterial();
        using var key = EncryptionKey.Create();
        using var signingA = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var signingB = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var signingC = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identityA = RecoveryTestMaterial.CreateIdentity(DeviceA, InstanceA, signingA);
        var identityB = RecoveryTestMaterial.CreateIdentity(DeviceB, InstanceB, signingB);
        var identityC = RecoveryTestMaterial.CreateIdentity(DeviceC, InstanceC, signingC);
        var sharedPasswordId = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
        var onlyAId = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAA1");
        var onlyBId = Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBB2");
        var onlyCId = Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCC3");
        var sharedWinnerVersion = RecoveryTestMaterial.Version(4_000, DeviceB, InstanceB);

        using var bundleA = material.CreateBundle(
            "stable-user",
            RecoveryTestMaterial.Version(1_000, DeviceA, InstanceA),
            [
                RecoveryTestMaterial.CreatePassword(sharedPasswordId, "shared-old", RecoveryTestMaterial.Version(1_100, DeviceA, InstanceA), 0x10),
                RecoveryTestMaterial.CreatePassword(onlyAId, "only-a", RecoveryTestMaterial.Version(1_200, DeviceA, InstanceA), 0x11)
            ]);
        using var bundleB = material.CreateBundle(
            "stable-user",
            RecoveryTestMaterial.Version(2_000, DeviceB, InstanceB),
            [
                RecoveryTestMaterial.CreatePassword(sharedPasswordId, "shared-winner", sharedWinnerVersion, 0x20),
                RecoveryTestMaterial.CreatePassword(onlyBId, "only-b", RecoveryTestMaterial.Version(2_100, DeviceB, InstanceB), 0x21)
            ]);
        using var bundleC = material.CreateBundle(
            "stable-user",
            RecoveryTestMaterial.Version(3_000, DeviceC, InstanceC),
            [RecoveryTestMaterial.CreatePassword(onlyCId, "only-c", RecoveryTestMaterial.Version(3_100, DeviceC, InstanceC), 0x31)]);

        var envelopes = new[]
        {
            await material.CreateEnvelopeAsync(bundleA, key, identityA, 7),
            await material.CreateEnvelopeAsync(bundleB, key, identityB, 5),
            await material.CreateEnvelopeAsync(bundleC, key, identityC, 9)
        };
        var permutations = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };

        string? expectedFingerprint = null;
        foreach (var permutation in permutations)
        {
            using var localBundle = material.CreateBundle(
                "damaged-local",
                RecoveryTestMaterial.Version(500, DeviceA, InstanceA));
            var local = await material.EncryptUserAsync(localBundle, key);
            local.EncryptedPayload[0] ^= 0x5A;
            var service = CreateService(new InMemoryUserRepository());

            var result = await service.TryReconstructCanonicalAsync(
                local,
                permutation.Select(index => envelopes[index]).ToArray(),
                key,
                UserSyncKeyConfidence.VerifiedRemoteSnapshot,
                expectedKeyEpoch: 1,
                expectedMembershipEpoch: 1);

            MSTestAssert.IsTrue(result.Reconstructed);
            MSTestAssert.AreEqual(3, result.HealthyCandidateCount);
            var fingerprint = await ReadFingerprintAsync(local, key);
            expectedFingerprint ??= fingerprint;
            MSTestAssert.AreEqual(expectedFingerprint, fingerprint);

            using var verified = await new UserDataBundleVerificationService(new UserDataBundleIntegrityService())
                .VerifyCanonicalAsync(local, key, UserSyncKeyConfidence.ExplicitlyTrusted);
            var shared = verified.VerifiedBundle!.UserPasswordsData.Passwords.Single(item => item.Id == sharedPasswordId);
            MSTestAssert.AreEqual("shared-winner", shared.Name);
            MSTestAssert.AreEqual(sharedWinnerVersion, shared.Version);
            CollectionAssert.AreEquivalent(
                new[] { sharedPasswordId, onlyAId, onlyBId, onlyCId },
                verified.VerifiedBundle.UserPasswordsData.Passwords.Select(item => item.Id).ToArray());
        }
    }

    [DataTestMethod]
    [DataRow(UserDataBlobKind.General)]
    [DataRow(UserDataBlobKind.Passwords)]
    [DataRow(UserDataBlobKind.Devices)]
    [TestCategory("Backend")]
    [TestCategory("Recovery")]
    public async Task TryReconstructCanonicalAsync_PartialSalvage_PreservesHealthyLocalComponents(
        UserDataBlobKind damagedComponent)
    {
        using var material = new RecoveryTestMaterial();
        using var key = EncryptionKey.Create();
        using var localSigning = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteSigning = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = RecoveryTestMaterial.CreateIdentity(DeviceA, InstanceA, localSigning);
        var remoteIdentity = RecoveryTestMaterial.CreateIdentity(DeviceB, InstanceB, remoteSigning);
        var localPasswordId = Guid.Parse("AAAAAAAA-1111-1111-1111-111111111111");
        var remotePasswordId = Guid.Parse("BBBBBBBB-2222-2222-2222-222222222222");
        var localDeviceId = Guid.Parse("AAAAAAAA-3333-3333-3333-333333333333");
        var remoteDeviceId = Guid.Parse("BBBBBBBB-4444-4444-4444-444444444444");
        var localGeneralVersion = RecoveryTestMaterial.Version(5_000, DeviceA, InstanceA);
        var localPasswordVersion = RecoveryTestMaterial.Version(5_100, DeviceA, InstanceA);
        var localDeviceVersion = RecoveryTestMaterial.Version(5_200, DeviceA, InstanceA);

        using var localBundle = material.CreateBundle(
            "local-healthy-name",
            localGeneralVersion,
            [RecoveryTestMaterial.CreatePassword(localPasswordId, "local-password", localPasswordVersion, 0x41)],
            [RecoveryTestMaterial.CreateDevice(localDeviceId, "local-device", localDeviceVersion)]);
        using var remoteBundle = material.CreateBundle(
            "remote-recovery-name",
            RecoveryTestMaterial.Version(1_000, DeviceB, InstanceB),
            [RecoveryTestMaterial.CreatePassword(remotePasswordId, "remote-password", RecoveryTestMaterial.Version(1_100, DeviceB, InstanceB), 0x51)],
            [RecoveryTestMaterial.CreateDevice(remoteDeviceId, "remote-device", RecoveryTestMaterial.Version(1_200, DeviceB, InstanceB))]);
        var local = await material.EncryptUserAsync(localBundle, key);
        var checkpoint = UserCanonicalCheckpointUtil.Create(local, 1, DateTimeOffset.UtcNow, localIdentity);
        var checkpoints = new MemoryCheckpointRepository(checkpoint);
        var remote = await material.CreateEnvelopeAsync(remoteBundle, key, remoteIdentity, revision: 3);

        switch (damagedComponent)
        {
            case UserDataBlobKind.General:
                local.EncryptedGeneralUserDataPayload[0] ^= 0x5A;
                break;
            case UserDataBlobKind.Passwords:
                local.EncryptedUserPasswordsDataPayload[0] ^= 0x5A;
                break;
            case UserDataBlobKind.Devices:
                local.EncryptedUserDevicesDataPayload[0] ^= 0x5A;
                break;
            default:
                MSTestAssert.Fail("The test component is unsupported.");
                break;
        }

        var service = CreateService(new InMemoryUserRepository(), checkpoints, localIdentity);
        var result = await service.TryReconstructCanonicalAsync(
            local,
            [remote],
            key,
            UserSyncKeyConfidence.ExplicitlyTrusted,
            expectedKeyEpoch: 1,
            expectedMembershipEpoch: 1);

        MSTestAssert.IsTrue(result.Reconstructed);
        MSTestAssert.AreEqual(damagedComponent, result.RecoveredComponents);
        using var verified = await new UserDataBundleVerificationService(new UserDataBundleIntegrityService())
            .VerifyCanonicalAsync(local, key, UserSyncKeyConfidence.ExplicitlyTrusted);
        MSTestAssert.IsTrue(verified.IsHealthy);
        var bundle = verified.VerifiedBundle!;

        if (damagedComponent != UserDataBlobKind.General)
        {
            MSTestAssert.AreEqual("local-healthy-name", bundle.GeneralUserData.Username);
            MSTestAssert.AreEqual(localGeneralVersion, bundle.GeneralUserData.Version);
        }
        else
        {
            MSTestAssert.AreEqual("remote-recovery-name", bundle.GeneralUserData.Username);
        }

        if (damagedComponent != UserDataBlobKind.Passwords)
        {
            var localPassword = bundle.UserPasswordsData.Passwords.Single(item => item.Id == localPasswordId);
            MSTestAssert.AreEqual(localPasswordVersion, localPassword.Version);
        }
        else
        {
            MSTestAssert.IsFalse(bundle.UserPasswordsData.Passwords.Any(item => item.Id == localPasswordId));
        }

        if (damagedComponent != UserDataBlobKind.Devices)
        {
            var localDevice = bundle.UserDevicesData.Devices.Single(item => item.Id == localDeviceId);
            MSTestAssert.AreEqual(localDeviceVersion, localDevice.Version);
        }
        else
        {
            MSTestAssert.IsFalse(bundle.UserDevicesData.Devices.Any(item => item.Id == localDeviceId));
        }
    }

    private static UserDataBundleSyncService CreateService(
        InMemoryUserRepository users,
        IUserCanonicalCheckpointRepository? checkpoints = null,
        FakeDeviceIdentityService? identity = null)
    {
        var integrity = new UserDataBundleIntegrityService();
        return new UserDataBundleSyncService(
            users,
            integrity,
            new UserPasswordsDataMergeService(),
            new UserDevicesDataMergeService(),
            new RecoveryTestVersionClock(),
            verification: new UserDataBundleVerificationService(integrity),
            checkpoints: checkpoints,
            identity: identity);
    }

    private static async Task<string> ReadFingerprintAsync(User user, EncryptionKey key)
    {
        using var verified = await new UserDataBundleVerificationService(new UserDataBundleIntegrityService())
            .VerifyCanonicalAsync(user, key, UserSyncKeyConfidence.ExplicitlyTrusted);
        MSTestAssert.IsTrue(verified.IsHealthy);
        var bundle = verified.VerifiedBundle!;
        return string.Join('|',
            bundle.GeneralUserData.Username,
            string.Join(',', bundle.UserPasswordsData.Passwords
                .OrderBy(item => item.Id)
                .Select(item => $"{item.Id:N}:{item.Name}:{item.Version.PhysicalTimeUnixMilliseconds}:{item.Version.LogicalCounter}:{item.Version.OriginDeviceId:N}:{item.Version.OriginInstanceId:N}")),
            string.Join(',', bundle.UserDevicesData.Devices
                .OrderBy(item => item.Id)
                .Select(item => $"{item.Id:N}:{item.Name}:{item.Version.PhysicalTimeUnixMilliseconds}:{item.Version.LogicalCounter}:{item.Version.OriginDeviceId:N}:{item.Version.OriginInstanceId:N}")));
    }

}
