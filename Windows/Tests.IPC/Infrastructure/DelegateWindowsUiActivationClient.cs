using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DelegateWindowsUiActivationClient : IWindowsUiActivationClient
{
    private readonly Func<UiActivationRequestDto, CancellationToken, Task<UiActivationResult>> _activate;

    public DelegateWindowsUiActivationClient(
        Func<UiActivationRequestDto, CancellationToken, Task<UiActivationResult>> activate)
    {
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
    }

    public int CallCount { get; private set; }

    public Task<UiActivationResult> TryActivateAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return _activate(request, cancellationToken);
    }
}
