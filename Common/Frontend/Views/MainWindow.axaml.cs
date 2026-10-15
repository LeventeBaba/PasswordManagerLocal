using System.IO;
using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.ViewModels;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "PasswordManagerLocal.ico");
        if (File.Exists(iconPath))
        {
            using var iconStream = File.OpenRead(iconPath);
            Icon = new WindowIcon(iconStream);
        }

        Deactivated += (_, _) =>
            (DataContext as MainViewModel)?.PlatformServices.SensitiveData.RequestHideVisibleSecrets();
    }
}
