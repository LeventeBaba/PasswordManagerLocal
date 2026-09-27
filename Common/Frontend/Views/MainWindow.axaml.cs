using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.ViewModels;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
            (DataContext as MainViewModel)?.PlatformServices.SensitiveData.RequestHideVisibleSecrets();
    }
}
