using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Common.Tests.Android.Runtime;

[TestClass]
public sealed class AndroidBackgroundRestorationPolicyTests
{
    private readonly AndroidBackgroundRestorationPolicy _policy = new();

    [TestMethod]
    public void UnsupportedTriggerIsIgnored()
    {
        var decision = _policy.Decide(
            AndroidBackgroundRestorationTrigger.Unsupported,
            isUserUnlocked: true,
            isBackgroundEnabled: true);

        Assert.AreEqual(AndroidBackgroundRestorationAction.Ignore, decision.Action);
        Assert.IsFalse(decision.ShouldReadPersistedSetting);
        Assert.IsFalse(decision.ShouldRequestServiceStart);
    }

    [DataTestMethod]
    [DataRow(AndroidBackgroundRestorationTrigger.BootCompleted)]
    [DataRow(AndroidBackgroundRestorationTrigger.PackageReplaced)]
    public void LockedManifestTriggerWaitsForEligibleLaunchWithoutReadingSetting(
        AndroidBackgroundRestorationTrigger trigger)
    {
        var decision = _policy.Decide(
            trigger,
            isUserUnlocked: false,
            isBackgroundEnabled: null);

        Assert.AreEqual(AndroidBackgroundRestorationAction.WaitForEligibleLaunch, decision.Action);
        Assert.IsFalse(decision.ShouldReadPersistedSetting);
        Assert.IsFalse(decision.ShouldRequestServiceStart);
    }

    [TestMethod]
    public void LockedStickyServiceDefersUntilUnlockWithoutReadingSetting()
    {
        var decision = _policy.Decide(
            AndroidBackgroundRestorationTrigger.StickyServiceRestart,
            isUserUnlocked: false,
            isBackgroundEnabled: null);

        Assert.AreEqual(AndroidBackgroundRestorationAction.DeferUntilUnlock, decision.Action);
        Assert.IsFalse(decision.ShouldReadPersistedSetting);
        Assert.IsFalse(decision.ShouldRequestServiceStart);
    }

    [TestMethod]
    public void UnlockedBootTriggerReadsSettingBeforeRequestingService()
    {
        var decision = _policy.Decide(
            AndroidBackgroundRestorationTrigger.BootCompleted,
            isUserUnlocked: true,
            isBackgroundEnabled: null);

        Assert.AreEqual(AndroidBackgroundRestorationAction.ReadPersistedSetting, decision.Action);
        Assert.IsTrue(decision.ShouldReadPersistedSetting);
        Assert.IsFalse(decision.ShouldRequestServiceStart);
    }

    [TestMethod]
    public void EnabledSettingRequestsSingleRestorationPath()
    {
        var decision = _policy.Decide(
            AndroidBackgroundRestorationTrigger.BootCompleted,
            isUserUnlocked: true,
            isBackgroundEnabled: true);

        Assert.AreEqual(
            AndroidBackgroundRestorationAction.RequestServiceRestoration,
            decision.Action);
        Assert.IsFalse(decision.ShouldReadPersistedSetting);
        Assert.IsTrue(decision.ShouldRequestServiceStart);
    }

    [TestMethod]
    public void DisabledSettingDoesNothing()
    {
        var decision = _policy.Decide(
            AndroidBackgroundRestorationTrigger.PackageReplaced,
            isUserUnlocked: true,
            isBackgroundEnabled: false);

        Assert.AreEqual(AndroidBackgroundRestorationAction.Ignore, decision.Action);
        Assert.IsFalse(decision.ShouldRequestServiceStart);
    }
}
