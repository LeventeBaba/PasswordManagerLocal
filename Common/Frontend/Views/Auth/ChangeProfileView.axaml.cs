using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Auth;

public partial class ChangeProfileView : UserControl
{
    public ChangeProfileView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
    }
}
