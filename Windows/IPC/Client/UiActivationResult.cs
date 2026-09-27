namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed record UiActivationResult(
    UiActivationResultKind Kind,
    string SafeMessage)
{
    public bool IsActivated => Kind == UiActivationResultKind.Activated;
}
