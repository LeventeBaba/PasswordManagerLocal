namespace PasswordManagerLocal.Windows.Agent.Ui;

public enum UiOpenResultKind
{
    Activated = 0,
    LaunchRequested = 1,
    ActivationRejected = 2,
    ActivationFailed = 3,
    ExecutableNotFound = 4,
    LaunchFailed = 5
}
