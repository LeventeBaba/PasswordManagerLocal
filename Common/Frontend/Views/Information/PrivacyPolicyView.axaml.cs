using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Information;

public partial class PrivacyPolicyView : UserControl
{
    private bool _isRestoring;

    public PrivacyPolicyView()
    {
        DeferredControlThemeResources.EnsureLoaded();
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

    private void GoToSection(object? sender, RoutedEventArgs e)
    {
        if (SectionPicker.SelectedItem is not PrivacyPolicySection section)
        {
            SectionPicker.Focus(NavigationMethod.Directional);
            return;
        }

        var container = PolicySections.ContainerFromIndex(SectionPicker.SelectedIndex);
        var heading = container?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => Equals(text.Tag, section.Id));
        if (heading is null || heading.TranslatePoint(default, PageScrollViewer) is not { } position)
            return;

        var offset = PageScrollViewer.Offset.Y + position.Y;
        heading.Focus(NavigationMethod.Directional);
        PageScrollViewer.Offset = new Vector(0, offset);
    }
}
