using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Information;

public partial class HelpPageView : UserControl
{
    private HelpPageViewModel? _page;
    private bool _isRestoring;
    private int _restoreRequestId;

    public HelpPageView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
        HelpSearchBox.AddHandler(KeyDownEvent, HandleSearchKeyDown, RoutingStrategies.Tunnel);
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
            else if (_page is not null && ReferenceEquals(e.Source, HelpSearchBox))
                _page.FocusedCardId = null;
        });
    }

    private void HandleSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Down) ||
            _page is not { IsHelpHome: true } page)
            return;
        // Move to results without opening a topic or activating an underlying task.
        e.Handled = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_page, page) || TopLevel.GetTopLevel(this) is null)
                return;
            Control target = FindCard(page.Cards.FirstOrDefault()?.Id) ?? (Control)SearchResultsStatus;
            target.Focus(NavigationMethod.Directional);
            target.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void HandleClearSearchClick(object? sender, RoutedEventArgs e)
    {
        var page = _page;
        // The command clears the query and hides its button. Restore focus after that update.
        Dispatcher.UIThread.Post(() =>
        {
            if (page is { IsHelpHome: true } && ReferenceEquals(_page, page) && TopLevel.GetTopLevel(this) is not null)
                HelpSearchBox.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    private Button? FindCard(string? id) => id is null ? null :
        this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag is string cardId && cardId == id);

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
        _restoreRequestId++;
        _isRestoring = false;
        if (_page is not null)
            _page.PropertyChanged -= HandlePageChanged;
        _page = null;
    }

    private void HandlePageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HelpPageViewModel.Cards) && _page is { } page)
            RestoreReadingPosition(page, restoreFocus: page.FocusedCardId is not null && !HelpSearchBox.IsKeyboardFocusWithin);
    }

    private void RestoreReadingPosition(HelpPageViewModel page, bool restoreFocus = true)
    {
        var offset = page.ScrollOffset;
        var requestId = ++_restoreRequestId;
        _isRestoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (requestId != _restoreRequestId)
                return;
            if (!ReferenceEquals(_page, page) || TopLevel.GetTopLevel(this) is null)
            {
                _isRestoring = false;
                return;
            }
            if (restoreFocus)
            {
                var card = FindCard(page.FocusedCardId);
                Control fallback = page.IsHelpHome && !page.IsAndroidUi ? HelpSearchBox : BackButton;
                (card ?? fallback).Focus(NavigationMethod.Directional);
            }
            PageScrollViewer.Offset = new Vector(0, offset);
            _isRestoring = false;
        }, DispatcherPriority.Loaded);
    }
}
