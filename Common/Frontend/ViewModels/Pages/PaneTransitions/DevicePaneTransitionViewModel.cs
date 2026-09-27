namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class DevicePaneTransitionViewModel
{
    protected DevicePaneTransitionViewModel(ProfileViewModel owner)
    {
        Owner = owner;
    }

    public ProfileViewModel Owner { get; }
}
