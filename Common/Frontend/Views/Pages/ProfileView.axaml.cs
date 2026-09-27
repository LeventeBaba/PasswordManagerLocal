using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages;

public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(ProfileMainScrollViewer);
    }
}
