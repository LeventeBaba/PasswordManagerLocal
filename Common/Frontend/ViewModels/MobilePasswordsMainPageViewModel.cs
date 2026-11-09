using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class MobilePasswordsMainPageViewModel : MobileMainPageViewModel
{
    public MobilePasswordsMainPageViewModel(PasswordsViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public PasswordsViewModel Owner { get; }
}
