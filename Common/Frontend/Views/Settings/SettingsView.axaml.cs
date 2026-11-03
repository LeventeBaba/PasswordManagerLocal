using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        DeferredControlThemeResources.EnsureLoaded();
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
    }
}
