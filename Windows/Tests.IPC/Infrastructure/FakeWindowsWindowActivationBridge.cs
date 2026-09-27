using PasswordManagerLocal.Windows.Frontend.Activation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsWindowActivationBridge : IWindowsWindowActivationBridge
{
    public bool Result { get; set; } = true;
    public int ActivationCount { get; private set; }

    public Task<bool> ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivationCount++;
        return Task.FromResult(Result);
    }
}
