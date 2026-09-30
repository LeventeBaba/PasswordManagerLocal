using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class MobileProfileMainPageViewModel : MobileMainPageViewModel
{
    public MobileProfileMainPageViewModel(ProfileViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public ProfileViewModel Owner { get; }
}
