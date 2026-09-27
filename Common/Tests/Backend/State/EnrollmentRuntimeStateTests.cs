using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.State;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.State;

[TestClass]
public sealed class EnrollmentRuntimeStateTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void ActivateAndDeactivate_AreIdempotent()
    {
        var state = new EnrollmentRuntimeState();

        MSTestAssert.IsFalse(state.IsActive);

        state.Activate();
        state.Activate();
        MSTestAssert.IsTrue(state.IsActive);

        state.Deactivate();
        state.Deactivate();
        MSTestAssert.IsFalse(state.IsActive);
    }
}
