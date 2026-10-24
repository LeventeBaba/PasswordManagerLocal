using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Auth;

public partial class RegistrationView : UserControl
{
    public RegistrationView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is not RegistrationViewModel { RestorePrivacyPolicyFocus: true } page)
                return;
            page.RestorePrivacyPolicyFocus = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(DataContext, page) && TopLevel.GetTopLevel(this) is not null)
                    PrivacyPolicyButton.Focus(NavigationMethod.Directional);
            }, DispatcherPriority.Loaded);
        };
    }
}
