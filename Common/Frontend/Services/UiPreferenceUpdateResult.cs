namespace PasswordManagerLocal.Common.Frontend.Services;

public readonly record struct UiPreferenceUpdateResult(
    UiPreferenceKind Kind,
    UiPreferenceUpdateStatus Status,
    bool AgentNotificationFailed = false,
    bool PersistenceStateUncertain = false)
{
    public bool IsSuccess =>
        Status is UiPreferenceUpdateStatus.Applied or UiPreferenceUpdateStatus.Unchanged;
}
