using System.IO;
using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.ViewModels;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "PasswordManagerLocalDemo.ico");
        using var iconStream = File.Exists(iconPath)
            ? File.OpenRead(iconPath)
            : typeof(MainWindow).Assembly.GetManifestResourceStream("PasswordManagerLocalDemo.ico");
        if (iconStream is not null)
            Icon = new WindowIcon(iconStream);

        Deactivated += (_, _) =>
            (DataContext as MainViewModel)?.PlatformServices.SensitiveData.RequestHideVisibleSecrets();
    }
}
