using PasswordManagerLocal.Common.Frontend.Views.Styling;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Profile;

public partial class ProfileTabsView : UserControl
{
    public ProfileTabsView()
    {
        DeferredControlThemeResources.EnsureLoaded();
        InitializeComponent();
    }
}
