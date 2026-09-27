using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IUiActivationRequestSink
{
    Task<bool> RequestActivationAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken);
}
