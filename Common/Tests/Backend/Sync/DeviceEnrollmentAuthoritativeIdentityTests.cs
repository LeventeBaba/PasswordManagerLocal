using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync;

[TestClass]
public sealed class DeviceEnrollmentAuthoritativeIdentityTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void CompletionProof_BindsExactTargetOriginInstance()
    {
        var sessionId = "ABCDEFGH";
        var secret = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var sourceDeviceId = Guid.NewGuid().ToString("N");
        var sourceOrigin = Guid.NewGuid();
        var targetDevice = Guid.NewGuid();
        var firstTargetOrigin = Guid.NewGuid();
        var secondTargetOrigin = Guid.NewGuid();
        var signPublicKey = Enumerable.Range(1, 32).Select(value => (byte)(value + 10)).ToArray();
        var fingerprint = new string('A', 64);

        var first = DeviceEnrollmentCode.BuildCompletionProof(
            sessionId, secret, sourceDeviceId, sourceOrigin, signPublicKey, fingerprint, targetDevice, firstTargetOrigin);
        var second = DeviceEnrollmentCode.BuildCompletionProof(
            sessionId, secret, sourceDeviceId, sourceOrigin, signPublicKey, fingerprint, targetDevice, secondTargetOrigin);

        MSTestAssert.IsFalse(first.SequenceEqual(second));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void SnapshotAad_BindsExactTargetOriginInstance()
    {
        var sessionId = "ABCDEFGH";
        var sourceDeviceId = Guid.NewGuid().ToString("N");
        var sourceOrigin = Guid.NewGuid();
        var targetDevice = Guid.NewGuid();
        var firstTargetOrigin = Guid.NewGuid();
        var secondTargetOrigin = Guid.NewGuid();
        var signPublicKey = Enumerable.Range(1, 32).Select(value => (byte)(value + 20)).ToArray();
        var fingerprint = new string('B', 64);

        var first = DeviceEnrollmentCode.BuildSnapshotEncryptionAad(
            sessionId, sourceDeviceId, sourceOrigin, signPublicKey, fingerprint, targetDevice, firstTargetOrigin);
        var second = DeviceEnrollmentCode.BuildSnapshotEncryptionAad(
            sessionId, sourceDeviceId, sourceOrigin, signPublicKey, fingerprint, targetDevice, secondTargetOrigin);

        MSTestAssert.IsFalse(first.SequenceEqual(second));
        StringAssert.Contains(Encoding.UTF8.GetString(first), firstTargetOrigin.ToString("N"));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Import_RejectsDefaultEpochsBeforeOpeningPersistenceScope()
    {
        var identity = CreateIdentity();
        var importer = new DeviceEnrollmentSnapshotImporterService(
            identity,
            new DeviceEnrollmentLocalLinkService(identity));
        using var services = new ServiceCollection()
            .AddSingleton<PasswordManagerLocal.Common.Backend.Abstractions.Repositories.IDeletedUserBarrierRepository, FakeDeletedUserBarrierRepository>()
            .BuildServiceProvider();
        var userId = Guid.NewGuid();
        var snapshot = CreateTargetedSnapshot(identity, userId);
        snapshot.Users.Add(new DeviceEnrollmentUserSnapshot
        {
            UId = userId,
            KeyEpoch = 0,
            MembershipEpoch = 0
        });

        await MSTestAssert.ThrowsExactlyAsync<InvalidDataException>(() =>
            importer.ImportAsync(services, snapshot, CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Import_RejectsBootstrapForAnotherInstallationOrigin()
    {
        var identity = CreateIdentity();
        var importer = new DeviceEnrollmentSnapshotImporterService(
            identity,
            new DeviceEnrollmentLocalLinkService(identity));
        using var services = new ServiceCollection()
            .AddSingleton<PasswordManagerLocal.Common.Backend.Abstractions.Repositories.IDeletedUserBarrierRepository, FakeDeletedUserBarrierRepository>()
            .BuildServiceProvider();
        var snapshot = CreateTargetedSnapshot(identity, Guid.NewGuid());
        snapshot.TargetOriginInstanceId = Guid.NewGuid();

        await MSTestAssert.ThrowsExactlyAsync<InvalidDataException>(() =>
            importer.ImportAsync(services, snapshot, CancellationToken.None));
    }

    private static FakeDeviceIdentityService CreateIdentity() => new()
    {
        LocalDeviceId = Guid.NewGuid(),
        OriginInstanceId = Guid.NewGuid(),
        SignPublicKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
        AgreementPublicKey = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray(),
        FingerprintHex = new string('C', 64),
        DeviceType = DeviceType.WindowsPc
    };

    private static DeviceEnrollmentSnapshot CreateTargetedSnapshot(FakeDeviceIdentityService identity, Guid userId) => new()
    {
        PrimaryUserId = userId,
        TargetDeviceId = identity.LocalDeviceId,
        TargetOriginInstanceId = identity.OriginInstanceId,
        TargetSignPublicKeyHash = Hashing.SHA256Hash(identity.SignPublicKey),
        TargetAgreementPublicKeyHash = Hashing.SHA256Hash(identity.AgreementPublicKey),
        TargetTlsCertFingerprint = identity.FingerprintHex,
        TargetDeviceType = identity.DeviceType
    };
}
