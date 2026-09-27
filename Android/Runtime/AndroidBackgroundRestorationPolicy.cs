namespace PasswordManagerLocal.Android.Runtime;

public sealed class AndroidBackgroundRestorationPolicy
{
    public AndroidBackgroundRestorationDecision Decide(
        AndroidBackgroundRestorationTrigger trigger,
        bool isUserUnlocked,
        bool? isBackgroundEnabled)
    {
        if (trigger == AndroidBackgroundRestorationTrigger.Unsupported)
        {
            return new AndroidBackgroundRestorationDecision(
                AndroidBackgroundRestorationAction.Ignore,
                ShouldReadPersistedSetting: false,
                ShouldRequestServiceStart: false);
        }

        if (!isUserUnlocked)
        {
            var action = trigger == AndroidBackgroundRestorationTrigger.StickyServiceRestart
                ? AndroidBackgroundRestorationAction.DeferUntilUnlock
                : AndroidBackgroundRestorationAction.WaitForEligibleLaunch;
            return new AndroidBackgroundRestorationDecision(
                action,
                ShouldReadPersistedSetting: false,
                ShouldRequestServiceStart: false);
        }

        if (!isBackgroundEnabled.HasValue)
        {
            return new AndroidBackgroundRestorationDecision(
                AndroidBackgroundRestorationAction.ReadPersistedSetting,
                ShouldReadPersistedSetting: true,
                ShouldRequestServiceStart: false);
        }

        return isBackgroundEnabled.Value
            ? new AndroidBackgroundRestorationDecision(
                AndroidBackgroundRestorationAction.RequestServiceRestoration,
                ShouldReadPersistedSetting: false,
                ShouldRequestServiceStart: true)
            : new AndroidBackgroundRestorationDecision(
                AndroidBackgroundRestorationAction.Ignore,
                ShouldReadPersistedSetting: false,
                ShouldRequestServiceStart: false);
    }
}
