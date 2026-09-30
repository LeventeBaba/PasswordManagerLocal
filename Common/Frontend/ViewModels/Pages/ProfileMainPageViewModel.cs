namespace PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

public abstract class ProfileMainPageViewModel(ProfileViewModel owner)
{
    public ProfileViewModel Owner { get; } = owner;
}

public sealed class ProfileAccountPageViewModel(ProfileViewModel owner) : ProfileMainPageViewModel(owner)
{
}

public sealed class ProfileDevicesPageViewModel(ProfileViewModel owner) : ProfileMainPageViewModel(owner)
{
}
