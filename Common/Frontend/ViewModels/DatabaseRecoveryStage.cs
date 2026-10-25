namespace PasswordManagerLocal.Common.Frontend.ViewModels;

internal enum DatabaseRecoveryStage
{
    None,
    RecoveryRequired,
    FinalConfirmation,
    Declined,
    ResetFailed
}
