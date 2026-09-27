namespace PasswordManagerLocal.Common.Frontend.ViewModels;

internal enum DatabaseRecoveryStage
{
    None,
    CompatibilityError,
    FinalConfirmation,
    Declined,
    ResetFailed
}
