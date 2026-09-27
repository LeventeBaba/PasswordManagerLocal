namespace PasswordManagerLocal.Windows.Agent.Native;

internal interface IWindowsNativeMessagePoster
{
    bool TryPostMessage(uint message, nuint wParam = 0, nint lParam = 0);
}
