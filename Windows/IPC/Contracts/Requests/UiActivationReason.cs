namespace PasswordManagerLocal.Windows.Ipc.Contracts.Requests;

public enum UiActivationReason
{
    UserLaunch = 1,
    Notification = 2,
    ExternalRequest = 3,
    TrayIcon = 4,
    AgentRequest = 5
}
