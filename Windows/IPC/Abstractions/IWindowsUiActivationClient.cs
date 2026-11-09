using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions;

public interface IWindowsUiActivationClient
{
    Task<UiActivationResult> TryActivateAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken = default);
}
