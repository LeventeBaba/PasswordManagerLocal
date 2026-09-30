using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public abstract class MobileMainPageViewModel
{
}

public sealed class MobilePasswordsMainPageViewModel : MobileMainPageViewModel
{
    public MobilePasswordsMainPageViewModel(PasswordsViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public PasswordsViewModel Owner { get; }
}

public sealed class MobileDevicesMainPageViewModel : MobileMainPageViewModel
{
    public MobileDevicesMainPageViewModel(ProfileViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public ProfileViewModel Owner { get; }
}

public sealed class MobileProfileMainPageViewModel : MobileMainPageViewModel
{
    public MobileProfileMainPageViewModel(ProfileViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public ProfileViewModel Owner { get; }
}
