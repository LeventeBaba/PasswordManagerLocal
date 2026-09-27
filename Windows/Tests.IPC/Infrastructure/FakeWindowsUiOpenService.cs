using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUiOpenService : IWindowsUiOpenService
{
    public UiOpenResult Result { get; set; } = new(
        UiOpenResultKind.Activated,
        "Activated.");
    public int OpenCount { get; private set; }

    public Task<UiOpenResult> OpenAsync(
        UiActivationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        return Task.FromResult(Result);
    }
}
