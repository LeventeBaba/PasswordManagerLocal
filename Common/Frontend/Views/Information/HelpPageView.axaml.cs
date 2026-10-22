using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Information;

public partial class HelpPageView : UserControl
{
    private HelpPageViewModel? _page;
    private bool _isRestoring;

    public HelpPageView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
        AttachedToVisualTree += (_, _) => BindPage();
        DetachedFromVisualTree += (_, _) => UnbindPage();
        DataContextChanged += (_, _) => BindPage();
        PageScrollViewer.ScrollChanged += (_, _) =>
        {
            if (_page is not null && !_isRestoring)
                _page.ScrollOffset = PageScrollViewer.Offset.Y;
        };
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (_page is not null && e.Source is Button { Tag: string id })
                _page.FocusedCardId = id;
        });
    }

    private void BindPage()
    {
        UnbindPage();
        if (DataContext is not HelpPageViewModel page)
            return;
        _page = page;
        _isRestoring = true;
        page.PropertyChanged += HandlePageChanged;
        RestoreReadingPosition(page);
    }

    private void UnbindPage()
    {
        if (_page is not null)
            _page.PropertyChanged -= HandlePageChanged;
        _page = null;
    }

    private void HandlePageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HelpPageViewModel.Cards) && _page is { } page)
            RestoreReadingPosition(page);
    }

    private void RestoreReadingPosition(HelpPageViewModel page)
    {
        var offset = page.ScrollOffset;
        _isRestoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_page, page) || TopLevel.GetTopLevel(this) is null)
                return;
            var card = this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Tag is string id && id == page.FocusedCardId);
            (card ?? BackButton).Focus(NavigationMethod.Directional);
            PageScrollViewer.Offset = new Vector(0, offset);
            _isRestoring = false;
        }, DispatcherPriority.Loaded);
    }
}
