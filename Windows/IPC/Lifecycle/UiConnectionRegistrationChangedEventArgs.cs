namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public sealed class UiConnectionRegistrationChangedEventArgs : EventArgs
{
    public UiConnectionRegistrationChangedEventArgs(
        UiConnectionRegistration? previous,
        UiConnectionRegistration? current)
    {
        Previous = previous;
        Current = current;
    }

    public UiConnectionRegistration? Previous { get; }
    public UiConnectionRegistration? Current { get; }
}
