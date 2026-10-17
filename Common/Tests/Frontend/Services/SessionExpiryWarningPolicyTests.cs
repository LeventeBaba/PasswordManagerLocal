using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Frontend.Services;

[TestClass]
public sealed class SessionExpiryWarningPolicyTests
{
    [TestMethod]
    public void WarningBeginsAtTheExactLeadTimeBoundary()
    {
        var expiresAt = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
        var leadTime = TimeSpan.FromMinutes(1);

        Assert.AreEqual(
            DateTimeOffset.Parse("2030-01-01T11:59:00Z"),
            SessionExpiryWarningPolicy.GetWarningDeadline(expiresAt, leadTime));
        Assert.IsFalse(SessionExpiryWarningPolicy.IsWarningDue(expiresAt.AddMinutes(-1).AddTicks(-1), expiresAt, leadTime));
        Assert.IsTrue(SessionExpiryWarningPolicy.IsWarningDue(expiresAt.AddMinutes(-1), expiresAt, leadTime));
        Assert.IsTrue(SessionExpiryWarningPolicy.IsWarningDue(expiresAt, expiresAt, leadTime));
    }

    [TestMethod]
    public void OneShotDeadlineRunsOnceAndCanBeCancelled()
    {
        var scheduler = new PasswordManagerLocal.Common.Tests.Fakes.ManualFrontendDeadlineScheduler();
        var deadline = scheduler.NowUtc.AddMinutes(1);
        var calls = 0;
        var timer = scheduler.Schedule(deadline, () => calls++);

        scheduler.AdvanceTo(deadline.AddTicks(-1));
        Assert.AreEqual(0, calls);
        scheduler.AdvanceTo(deadline);
        scheduler.AdvanceTo(deadline.AddMinutes(1));
        Assert.AreEqual(1, calls);

        var canceledCalls = 0;
        var canceled = scheduler.Schedule(scheduler.NowUtc.AddMinutes(1), () => canceledCalls++);
        canceled.Dispose();
        scheduler.AdvanceTo(scheduler.NowUtc.AddMinutes(2));
        Assert.AreEqual(0, canceledCalls);
        timer.Dispose();
    }
}
