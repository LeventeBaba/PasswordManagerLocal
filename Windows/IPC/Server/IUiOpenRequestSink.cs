using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IUiOpenRequestSink
{
    Task<bool> RequestOpenAsync(
        UiOpenRequestDto request,
        CancellationToken cancellationToken);
}
