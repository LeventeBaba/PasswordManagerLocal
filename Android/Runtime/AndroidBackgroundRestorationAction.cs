namespace PasswordManagerLocal.Android.Runtime;

public enum AndroidBackgroundRestorationAction
{
    Ignore,
    WaitForEligibleLaunch,
    DeferUntilUnlock,
    ReadPersistedSetting,
    RequestServiceRestoration
}
