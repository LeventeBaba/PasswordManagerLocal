namespace PasswordManagerLocal.Common.Frontend.Services;

public enum UiPreferenceFailureStage
{
    LoadLocalization,
    Persist,
    RollbackPersistence,
    NotifyAgent,
    Observer
}
