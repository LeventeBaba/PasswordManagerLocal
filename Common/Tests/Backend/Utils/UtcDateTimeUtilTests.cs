using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
public sealed class UtcDateTimeUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("UtcDates")]
    public void ToUtc_TreatsUnspecifiedDateTimeAsUtc()
    {
        var unspecified = new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Unspecified);

        var utc = UtcDateTimeUtil.ToUtc(unspecified);

        MSTestAssert.AreEqual(DateTimeKind.Utc, utc.Kind);
        MSTestAssert.AreEqual(unspecified.Ticks, utc.Ticks);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("UtcDates")]
    public void ToUtc_NormalizesDateTimeOffsetToZeroOffset()
    {
        var offsetValue = new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.FromHours(2));

        var utc = UtcDateTimeUtil.ToUtc(offsetValue);

        MSTestAssert.AreEqual(TimeSpan.Zero, utc.Offset);
        MSTestAssert.AreEqual(10, utc.Hour);
        MSTestAssert.AreEqual(30, utc.Minute);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("UtcDates")]
    public void NormalizeObjectGraph_NormalizesNestedDateValues()
    {
        var data = new UserPasswordsData
        {
            Passwords =
            [
                new SecurePassword
                {
                    CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Unspecified),
                    LastUpdatedAt = new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Unspecified)
                }
            ],
            DeletedPasswords =
            [
                new DeletedPasswordData
                {
                    DeletedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified)
                }
            ]
        };

        UtcDateTimeUtil.NormalizeObjectGraph(data);

        MSTestAssert.AreEqual(DateTimeKind.Utc, data.Passwords[0].CreatedAt.Kind);
        MSTestAssert.AreEqual(DateTimeKind.Utc, data.Passwords[0].LastUpdatedAt.Kind);
        MSTestAssert.AreEqual(DateTimeKind.Utc, data.DeletedPasswords[0].DeletedAt.Kind);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    [TestCategory("UtcDates")]
    public void DateTimeHashing_IsStableForUtcAndEquivalentUnspecifiedValues()
    {
        var utc = new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        var utcHash = Hashing.SHA256Hash(hash => hash.Write(utc));
        var unspecifiedHash = Hashing.SHA256Hash(hash => hash.Write(unspecified));

        CollectionAssert.AreEqual(utcHash, unspecifiedHash);
    }
}
