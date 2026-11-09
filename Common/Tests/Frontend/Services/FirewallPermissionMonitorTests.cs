using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Frontend.Services;

[TestClass]
public sealed class FirewallPermissionMonitorTests
{
    [TestMethod]
    public async Task MonitoringPublishesBlockedThenAllowThenConfiguredWithoutRestarting()
    {
        var blocked = new FirewallPermissionCheckResult { IsSupported = true };
        var canAllow = new FirewallPermissionCheckResult { IsSupported = true, CanRequestPermission = true };
        var configured = new FirewallPermissionCheckResult { IsSupported = true, IsConfigured = true };
        var checks = new Queue<FirewallPermissionCheckResult>([blocked, canAllow, configured]);
        var observed = new List<FirewallPermissionCheckResult>();

        await FirewallPermissionStartupPrompt.MonitorFirewallAsync(
            _ => Task.FromResult(checks.Dequeue()), observed.Add, TimeSpan.Zero, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { blocked, canAllow, configured }, observed);
        Assert.AreEqual(0, checks.Count);
    }

    [TestMethod]
    public async Task CheckCompletingAfterDismissalDoesNotUpdateTheDialog()
    {
        using var dismissed = new CancellationTokenSource();
        var observed = new List<FirewallPermissionCheckResult>();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            FirewallPermissionStartupPrompt.MonitorFirewallAsync(
                _ =>
                {
                    dismissed.Cancel();
                    return Task.FromResult(new FirewallPermissionCheckResult
                    {
                        IsSupported = true,
                        CanRequestPermission = true
                    });
                },
                observed.Add,
                TimeSpan.Zero,
                dismissed.Token));

        Assert.AreEqual(0, observed.Count);
    }
}
