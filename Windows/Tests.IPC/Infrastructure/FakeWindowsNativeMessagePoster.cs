using PasswordManagerLocal.Windows.Agent.Native;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsNativeMessagePoster : IWindowsNativeMessagePoster
{
    public int PostCount { get; private set; }
    public uint LastMessage { get; private set; }
    public bool ShouldSucceed { get; set; } = true;

    public bool TryPostMessage(uint message, nuint wParam = 0, nint lParam = 0)
    {
        PostCount++;
        LastMessage = message;
        return ShouldSucceed;
    }
}
