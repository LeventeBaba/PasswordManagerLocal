using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.RegularExpressions;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
public sealed class DeviceIdentityUtilityTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void BuildDefaultDeviceName_IsDeterministicAndUsesRestrictedFormat()
    {
        var deviceId = Guid.Parse("5027C8B6-41A8-4475-A204-2E01E5B90358");

        var first = DeviceNameUtil.BuildDefaultDeviceName(deviceId);
        var second = DeviceNameUtil.BuildDefaultDeviceName(deviceId);

        MSTestAssert.AreEqual(first, second);
        MSTestAssert.IsTrue(Regex.IsMatch(first, "^Device-[A-Z0-9]{6}$", RegexOptions.CultureInvariant));
        MSTestAssert.AreNotEqual(first, DeviceNameUtil.BuildDefaultDeviceName(Guid.Parse("11111111-1111-1111-1111-111111111111")));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void BuildUserDeviceModelId_IsDeterministicAndBindsBothIdsInOrder()
    {
        var userId = Guid.Parse("083A9389-44C5-4705-A131-77DE88BEA880");
        var deviceId = Guid.Parse("28DF1C09-01A3-470B-B013-92786542E941");

        var first = SyncIdentityUtil.BuildUserDeviceModelId(userId, deviceId);
        var second = SyncIdentityUtil.BuildUserDeviceModelId(userId, deviceId);
        var reversed = SyncIdentityUtil.BuildUserDeviceModelId(deviceId, userId);
        var differentDevice = SyncIdentityUtil.BuildUserDeviceModelId(userId, Guid.Parse("11111111-1111-1111-1111-111111111111"));

        MSTestAssert.AreEqual(first, second);
        MSTestAssert.AreNotEqual(Guid.Empty, first);
        MSTestAssert.AreNotEqual(first, reversed);
        MSTestAssert.AreNotEqual(first, differentDevice);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void DeviceTypeDetector_OnlyAcceptsSupportedRuntimeTypes()
    {
        MSTestAssert.IsTrue(DeviceTypeDetector.IsValid(DeviceType.WindowsPc));
        MSTestAssert.IsTrue(DeviceTypeDetector.IsValid(DeviceType.AndroidMobile));
        MSTestAssert.IsFalse(DeviceTypeDetector.IsValid(DeviceType.Unknown));
    }
}
