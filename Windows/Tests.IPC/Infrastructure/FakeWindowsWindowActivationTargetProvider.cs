using PasswordManagerLocal.Windows.Frontend.Activation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsWindowActivationTargetProvider : IWindowsWindowActivationTargetProvider
{
    public IWindowsWindowActivationTarget? Target { get; set; }
    public int GetTargetCount { get; private set; }

    public IWindowsWindowActivationTarget? GetTarget()
    {
        GetTargetCount++;
        return Target;
    }
}
