namespace PasswordManagerLocal.Common.Frontend.ViewModels;

public sealed class MainPageContentViewModel
{
    public MainPageContentViewModel(ViewModelBase pageViewModel)
    {
        PageViewModel = pageViewModel;
    }

    public ViewModelBase PageViewModel { get; }
}
