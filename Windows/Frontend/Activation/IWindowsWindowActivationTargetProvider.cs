namespace PasswordManagerLocal.Windows.Frontend.Activation;

public interface IWindowsWindowActivationTargetProvider
{
    IWindowsWindowActivationTarget? GetTarget();
}
