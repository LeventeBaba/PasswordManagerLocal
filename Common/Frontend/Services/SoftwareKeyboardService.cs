namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class SoftwareKeyboardService
{
    private Action? _hidePlatformKeyboard;

    public void SetPlatformHideAction(Action? hidePlatformKeyboard) =>
        _hidePlatformKeyboard = hidePlatformKeyboard;

    public void Hide()
    {
        try
        {
            _hidePlatformKeyboard?.Invoke();
        }
        catch
        {
        }
    }
}
