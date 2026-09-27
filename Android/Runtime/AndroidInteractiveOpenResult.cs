namespace PasswordManagerLocal.Android.Runtime;

internal sealed record AndroidInteractiveOpenResult(
    bool IsConnected,
    bool IsRuntimeSafe,
    Exception? Failure);
