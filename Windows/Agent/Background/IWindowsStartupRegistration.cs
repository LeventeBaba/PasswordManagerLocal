namespace PasswordManagerLocal.Windows.Agent.Background;

public interface IWindowsStartupRegistration
{
    Task<WindowsStartupRegistrationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default);
    Task RegisterAsync(CancellationToken cancellationToken = default);
    Task UnregisterAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(
        WindowsStartupRegistrationSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
