using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
public sealed class UserDeviceLoginUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public void UpdateCurrentDeviceLastLoginDate_ShiftsTheExistingLastLoginIntoPreviousLogin()
    {
        var deviceId = Guid.NewGuid();
        var previousLogin = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var currentLogin = new DateTimeOffset(2026, 1, 3, 4, 5, 6, TimeSpan.Zero);
        using var data = new UserDevicesData
        {
            Devices =
            [
                new UserDeviceData
                {
                    Id = deviceId,
                    Name = "Test device",
                    LinkedAt = DateTimeOffset.UnixEpoch,
                    LastLoginDate = previousLogin,
                    LastUpdatedAt = DateTimeOffset.UnixEpoch,
                    Version = Version(1)
                }
            ]
        };
        data.Devices[0].GenerateIntegrityHash();

        UserDeviceLoginUtil.UpdateCurrentDeviceLastLoginDate(data, deviceId, currentLogin, Version(2));

        var device = data.Devices.Single();
        MSTestAssert.AreEqual(previousLogin, device.PreviousLoginDate);
        MSTestAssert.AreEqual(currentLogin.UtcDateTime, device.LastLoginDate);
        MSTestAssert.IsTrue(device.IsIntegrityValid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void UpdateCurrentDeviceLastLoginDate_FirstRecordedLoginLeavesPreviousLoginEmpty()
    {
        var deviceId = Guid.NewGuid();
        var loginTime = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        using var data = new UserDevicesData();

        UserDeviceLoginUtil.UpdateCurrentDeviceLastLoginDate(data, deviceId, loginTime, Version(1));

        var device = data.Devices.Single();
        MSTestAssert.IsNull(device.PreviousLoginDate);
        MSTestAssert.AreEqual(loginTime.UtcDateTime, device.LastLoginDate);
        MSTestAssert.IsTrue(device.IsIntegrityValid());
    }

    private static SyncVersionStamp Version(long physicalTime) => new()
    {
        PhysicalTimeUnixMilliseconds = physicalTime,
        LogicalCounter = 0,
        OriginDeviceId = Guid.Parse("72D0125D-565D-42D6-90F4-1317DAB61513"),
        OriginInstanceId = Guid.Parse("3EAB501E-2F3D-43D1-8368-87012878C343")
    };
}
