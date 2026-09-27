using PasswordManagerLocal.Windows.Frontend.Activation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUiShutdownBridge : IWindowsUiShutdownBridge
{
    public bool Result { get; set; } = true;
    public int ShutdownCount { get; private set; }

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownCount++;
        return Task.FromResult(Result);
    }
}
