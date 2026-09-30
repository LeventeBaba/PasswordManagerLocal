namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class ProfileMainPageViewModel(ProfileViewModel owner)
{
    public ProfileViewModel Owner { get; } = owner;
}
