using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class MobileDevicesMainPageViewModel : MobileMainPageViewModel
{
    public MobileDevicesMainPageViewModel(ProfileViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public ProfileViewModel Owner { get; }
}
