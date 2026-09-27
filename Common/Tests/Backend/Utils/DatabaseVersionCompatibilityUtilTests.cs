using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Utils;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
public sealed class DatabaseVersionCompatibilityUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void IsIncomingDatabaseVersionSupported_AcceptsSupportedRange()
    {
        MSTestAssert.IsTrue(DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(DatabaseConstants.OldestSupportedDbVersion));
        MSTestAssert.IsTrue(DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(DatabaseConstants.CurrentDbVersion));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void IsIncomingDatabaseVersionSupported_RejectsTooOldAndTooNew()
    {
        MSTestAssert.IsFalse(DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(DatabaseConstants.OldestSupportedDbVersion - 1));
        MSTestAssert.IsFalse(DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(DatabaseConstants.CurrentDbVersion + 1));
    }
}
