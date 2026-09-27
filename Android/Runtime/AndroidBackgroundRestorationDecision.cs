namespace PasswordManagerLocal.Android.Runtime;

public sealed record AndroidBackgroundRestorationDecision(
    AndroidBackgroundRestorationAction Action,
    bool ShouldReadPersistedSetting,
    bool ShouldRequestServiceStart);
