using PasswordManagerLocal.Windows.Agent.Ui;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUiLauncher : IWindowsUiLauncher
{
    public UiLaunchResult Result { get; set; } = new(
        UiLaunchResultKind.LaunchRequested,
        "Launch requested.");
    public int LaunchCount { get; private set; }

    public Task<UiLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LaunchCount++;
        return Task.FromResult(Result);
    }
}
