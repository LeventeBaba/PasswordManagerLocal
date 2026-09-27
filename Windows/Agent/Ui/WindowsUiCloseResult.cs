namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed record WindowsUiCloseResult(
    WindowsUiCloseResultKind Kind,
    string SafeMessage)
{
    public bool IsAcknowledged => Kind == WindowsUiCloseResultKind.Acknowledged;
}
