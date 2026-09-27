using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Auth;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
    }
}
