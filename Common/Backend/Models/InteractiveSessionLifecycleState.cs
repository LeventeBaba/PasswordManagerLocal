namespace PasswordManagerLocal.Common.Backend.Models;

public enum InteractiveSessionLifecycleState
{
    None,
    Opening,
    Active,
    Closing,
    CleanupFailed
}
