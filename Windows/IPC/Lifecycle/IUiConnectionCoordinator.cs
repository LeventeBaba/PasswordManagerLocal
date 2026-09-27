namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public interface IUiConnectionCoordinator
{
    Guid? RegisteredConnectionId { get; }
    UiConnectionRegistration? Registration { get; }
    event EventHandler<UiConnectionRegistrationChangedEventArgs>? RegistrationChanged;

    bool TryRegister(IpcConnectionContext connection, out UiConnectionRegistration registration);
    bool Unregister(Guid connectionId);
    bool IsCurrentRegistration(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long? generation = null);
    bool TryBeginIntentionalShutdown(out UiConnectionRegistration? registration);
    void CancelIntentionalShutdown();
}
