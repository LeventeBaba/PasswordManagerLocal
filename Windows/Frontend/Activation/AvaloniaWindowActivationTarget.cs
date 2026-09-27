using Avalonia.Controls;

namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class AvaloniaWindowActivationTarget : IWindowsWindowActivationTarget
{
    private readonly Window _window;

    public AvaloniaWindowActivationTarget(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public bool IsMinimized => _window.WindowState == WindowState.Minimized;
    public bool IsVisible => _window.IsVisible;

    public void Restore() => _window.WindowState = WindowState.Normal;
    public void Show() => _window.Show();
    public void Activate() => _window.Activate();
    public void Focus() => _window.Focus();
}
