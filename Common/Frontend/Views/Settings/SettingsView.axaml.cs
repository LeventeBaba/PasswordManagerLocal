using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
    }
}
