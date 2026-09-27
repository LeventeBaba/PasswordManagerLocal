namespace PasswordManagerLocal.Common.Contracts.Runtime;

[Flags]
public enum BackendLifetimeReason
{
    None = 0,
    InteractiveUi = 1,
    BackgroundSync = 2
}
