using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public interface IWindowsUiOpenService
{
    Task<UiOpenResult> OpenAsync(
        UiActivationReason reason,
        CancellationToken cancellationToken = default);
}
