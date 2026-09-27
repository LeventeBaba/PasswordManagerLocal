namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class PasswordPaneTransitionViewModel
{
    protected PasswordPaneTransitionViewModel(PasswordsViewModel owner)
    {
        Owner = owner;
    }

    public PasswordsViewModel Owner { get; }
}
