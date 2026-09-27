using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncDeviceIdentityServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryAdd_TrustedRemoteDevice_CanBeRetrievedWithoutExposingStoredInstance()
    {
        var identity = CreateIdentity();
        using var service = new SyncDeviceIdentityService(identity);
        var device = CreateRemoteDevice("AA:BB:CC");

        MSTestAssert.IsTrue(service.TryAdd(device));
        MSTestAssert.AreEqual(1, service.Count());
        MSTestAssert.IsTrue(service.ContainsId(device.Id));
        MSTestAssert.IsTrue(service.ContainsFingerprint("aabbcc"));
        MSTestAssert.IsTrue(service.TryGetById(device.Id, out var retrieved));
        MSTestAssert.IsNotNull(retrieved);
        MSTestAssert.AreNotSame(device, retrieved);

        retrieved.IsBlocked = true;
        retrieved.PublicKey[0] = 99;

        MSTestAssert.IsTrue(service.TryGetByFingerprint("AA BB CC", out var second));
        MSTestAssert.IsNotNull(second);
        MSTestAssert.IsFalse(second.IsBlocked);
        MSTestAssert.AreEqual((byte)1, second.PublicKey[0]);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task TryAdd_RejectsDisabledUntrustedBlockedAndLocalDevices()
    {
        var identity = CreateIdentity();
        using var service = new SyncDeviceIdentityService(identity);

        await identity.SetSyncOnAsync(false);
        MSTestAssert.IsFalse(service.TryAdd(CreateRemoteDevice("AA")));

        await identity.SetSyncOnAsync(true);
        var untrusted = CreateRemoteDevice("BB");
        untrusted.IsTrusted = false;
        MSTestAssert.IsFalse(service.TryAdd(untrusted));

        var blocked = CreateRemoteDevice("CC");
        blocked.IsBlocked = true;
        MSTestAssert.IsFalse(service.TryAdd(blocked));

        var localById = CreateRemoteDevice("DD");
        localById.Id = identity.LocalDeviceId;
        MSTestAssert.IsFalse(service.TryAdd(localById));

        var localByFingerprint = CreateRemoteDevice(identity.FingerprintHex);
        MSTestAssert.IsFalse(service.TryAdd(localByFingerprint));

        var localBySigningKey = CreateRemoteDevice("EE");
        localBySigningKey.SignPublicKey = identity.SignPublicKey.ToArray();
        MSTestAssert.IsFalse(service.TryAdd(localBySigningKey));

        MSTestAssert.IsTrue(service.IsEmpty());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryAdd_UpdatingFingerprint_RemovesOldFingerprintMapping()
    {
        var identity = CreateIdentity();
        using var service = new SyncDeviceIdentityService(identity);
        var device = CreateRemoteDevice("AA11");

        MSTestAssert.IsTrue(service.TryAdd(device));
        device.TlsCertFingerprint = "BB22";
        MSTestAssert.IsTrue(service.TryAdd(device));

        MSTestAssert.IsFalse(service.ContainsFingerprint("AA11"));
        MSTestAssert.IsTrue(service.ContainsFingerprint("BB22"));
        MSTestAssert.AreEqual(1, service.Count());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryAdd_DuplicateFingerprint_ReplacesPreviousDevice()
    {
        var identity = CreateIdentity();
        using var service = new SyncDeviceIdentityService(identity);
        var first = CreateRemoteDevice("AABB");
        var second = CreateRemoteDevice("aa:bb");

        MSTestAssert.IsTrue(service.TryAdd(first));
        MSTestAssert.IsTrue(service.TryAdd(second));

        MSTestAssert.AreEqual(1, service.Count());
        MSTestAssert.IsFalse(service.ContainsId(first.Id));
        MSTestAssert.IsTrue(service.ContainsId(second.Id));
        MSTestAssert.IsTrue(service.TryGetByFingerprint("AA BB", out var found));
        MSTestAssert.AreEqual(second.Id, found?.Id);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryRemoveAndClear_RemoveAllMappings()
    {
        var identity = CreateIdentity();
        using var service = new SyncDeviceIdentityService(identity);
        var first = CreateRemoteDevice("11");
        var second = CreateRemoteDevice("22");

        MSTestAssert.AreEqual(2, service.TryAdd([first, second]));
        MSTestAssert.IsTrue(service.TryRemove(new Device { Id = Guid.Empty, TlsCertFingerprint = "11" }));
        MSTestAssert.IsFalse(service.ContainsId(first.Id));
        MSTestAssert.IsFalse(service.ContainsFingerprint("11"));

        service.Clear();
        MSTestAssert.IsTrue(service.IsEmpty());
        MSTestAssert.IsFalse(service.ContainsFingerprint("22"));
    }

    private static FakeDeviceIdentityService CreateIdentity() =>
        new()
        {
            IsInitialized = true,
            LocalDeviceId = Guid.NewGuid(),
            FingerprintHex = "LOCALFF",
            SignPublicKey = Enumerable.Repeat((byte)9, 32).ToArray(),
            AgreementPublicKey = Enumerable.Repeat((byte)8, 32).ToArray()
        };

    private static Device CreateRemoteDevice(string fingerprint) =>
        new()
        {
            Id = Guid.NewGuid(),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Enumerable.Repeat((byte)2, 32).ToArray(),
            TlsCertFingerprint = fingerprint,
            IsTrusted = true,
            IsBlocked = false,
            DeviceType = DeviceType.WindowsPc
        };
}
