using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Information;

public partial class PrivacyPolicyView : UserControl
{
    private bool _isRestoring;

    public PrivacyPolicyView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is not PrivacyPolicyViewModel page)
                return;
            var offset = page.ScrollOffset;
            _isRestoring = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(DataContext, page) && TopLevel.GetTopLevel(this) is not null)
                {
                    BackButton.Focus(NavigationMethod.Directional);
                    PageScrollViewer.Offset = new Vector(0, offset);
                }
                _isRestoring = false;
            }, DispatcherPriority.Loaded);
        };
        PageScrollViewer.ScrollChanged += (_, _) =>
        {
            if (!_isRestoring && DataContext is PrivacyPolicyViewModel page)
                page.ScrollOffset = PageScrollViewer.Offset.Y;
        };
    }
}
