namespace PasswordManagerLocal.Windows.Ipc.Client.Activation;

public sealed record UiActivationResult(
    UiActivationResultKind Kind,
    string SafeMessage)
{
    public bool IsActivated => Kind == UiActivationResultKind.Activated;
}
