namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed record UiOpenResult(UiOpenResultKind Kind, string SafeMessage)
{
    public bool IsSuccess => Kind is UiOpenResultKind.Activated or UiOpenResultKind.LaunchRequested;
}
