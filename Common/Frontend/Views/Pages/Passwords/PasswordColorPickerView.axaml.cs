using PasswordManagerLocal.Common.Frontend.Views.Styling;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordColorPickerView : UserControl
{
    public PasswordColorPickerView()
    {
        DeferredControlThemeResources.EnsureLoaded();
        InitializeComponent();
    }
}
