namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsWindowActivationTarget
{
    bool IsMinimized { get; }
    bool IsVisible { get; }

    void Restore();
    void Show();
    void Activate();
    void Focus();
}
