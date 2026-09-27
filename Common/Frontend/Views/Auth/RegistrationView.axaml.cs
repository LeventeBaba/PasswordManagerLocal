using Avalonia.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Auth;

public partial class RegistrationView : UserControl
{
    public RegistrationView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
    }
}
