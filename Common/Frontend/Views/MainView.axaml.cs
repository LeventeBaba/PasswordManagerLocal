using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views.Behaviors;
using System.ComponentModel;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class MainView : UserControl, IDisposable
{
    private readonly MainViewKeyboardHandler _keyboardHandler;
    private readonly MainViewSwipeNavigationHandler _swipeNavigationHandler;
    private readonly MainViewTapOutsideKeyboardDismissHandler _tapOutsideKeyboardDismissHandler;
    private readonly MainViewLongPressToolTipHandler _longPressToolTipHandler;
    private readonly PageSlide _mobileMainPageTransition;
    private bool _disposed;
    internal FrontendPlatformServices? PlatformServices => (DataContext as MainViewModel)?.PlatformServices;
    private TopLevel? _inputTopLevel;
    private MainViewModel? _observedViewModel;

    public MainView()
    {
        InitializeComponent();
        _mobileMainPageTransition = new PageSlide(
            TimeSpan.FromMilliseconds(280),
            PageSlide.SlideAxis.Horizontal)
        {
            SlideInEasing = new CubicEaseOut(),
            SlideOutEasing = new CubicEaseOut()
        };
        _keyboardHandler = new MainViewKeyboardHandler(this);
        _swipeNavigationHandler = new MainViewSwipeNavigationHandler(this);
        _tapOutsideKeyboardDismissHandler = new MainViewTapOutsideKeyboardDismissHandler(this);
        _longPressToolTipHandler = new MainViewLongPressToolTipHandler(this);
        RegisterLocalInputHandlers();
        RegisterLifecycleHandlers();
        HandleDataContextChanged(this, EventArgs.Empty);
    }

    private void RegisterLocalInputHandlers()
    {
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(
            TextBox.CopyingToClipboardEvent,
            HandleCopyingToClipboard,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble | RoutingStrategies.Direct);
        AddHandler(
            TextBox.CuttingToClipboardEvent,
            HandleCuttingToClipboard,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble | RoutingStrategies.Direct);
    }

    private void RegisterLifecycleHandlers()
    {
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
        DataContextChanged += HandleDataContextChanged;
    }

    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HandleDataContextChanged(this, EventArgs.Empty);
        var topLevel = TopLevel.GetTopLevel(this);
        SetActiveTopLevelForUiServices(topLevel);

        if (ReferenceEquals(_inputTopLevel, topLevel))
            return;

        DetachTopLevelInputHandlers();
        AttachTopLevelInputHandlers(topLevel);
    }

    private void SetActiveTopLevelForUiServices(TopLevel? topLevel)
    {
        PlatformServices?.Clipboard.SetActiveTopLevel(topLevel);
        PlatformServices?.ImagePicker.SetActiveTopLevel(topLevel);
        if (OperatingSystem.IsWindows())
            FirewallPermissionStartupPrompt.SetActiveTopLevel(topLevel);
    }

    private void AttachTopLevelInputHandlers(TopLevel? topLevel)
    {
        if (topLevel is null)
            return;

        _inputTopLevel = topLevel;
        topLevel.AddHandler(KeyDownEvent, HandleTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (!OperatingSystem.IsAndroid())
            return;

        topLevel.AddHandler(PointerPressedEvent, HandlePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerMovedEvent, HandlePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, HandlePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, HandlePointerCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
    }

    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SetActiveTopLevelForUiServices(null);
        DetachTopLevelInputHandlers();
        DetachObservedViewModel();
    }

    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        DetachObservedViewModel();
        if (_disposed || DataContext is not MainViewModel viewModel)
            return;

        _observedViewModel = viewModel;
        SetActiveTopLevelForUiServices(TopLevel.GetTopLevel(this));
        _observedViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        UpdateMobileMainPageTransition();
    }

    private void DetachObservedViewModel()
    {
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        _observedViewModel = null;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var loggedOut = e.PropertyName == nameof(MainViewModel.IsAuthenticated)
            && sender is MainViewModel { IsAuthenticated: false };
        if (loggedOut || e.PropertyName == nameof(MainViewModel.CurrentUserDisplayName))
            HideAccountMenuFlyout();

        if (e.PropertyName == nameof(MainViewModel.IsMobileMainPageAnimationEnabled))
            UpdateMobileMainPageTransition();
    }

    private void UpdateMobileMainPageTransition()
    {
        var carousel = this.FindControl<Carousel>("MobileMainCarousel");
        if (carousel is null)
            return;

        carousel.PageTransition = _observedViewModel?.IsMobileMainPageAnimationEnabled == true
            ? _mobileMainPageTransition
            : null;
    }

    private void HandleAccountMenuActionClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(HideAccountMenuFlyout);

    private void HideAccountMenuFlyout()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideAccountMenuFlyout);
            return;
        }

        this.FindControl<Button>("AccountMenuButton")?.Flyout?.Hide();
        this.FindControl<Button>("MobileAccountMenuButton")?.Flyout?.Hide();
    }

    private void DetachTopLevelInputHandlers()
    {
        if (_inputTopLevel is not null)
        {
            _inputTopLevel.RemoveHandler(KeyDownEvent, HandleTopLevelKeyDown);
            if (OperatingSystem.IsAndroid())
            {
                _inputTopLevel.RemoveHandler(PointerPressedEvent, HandlePointerPressed);
                _inputTopLevel.RemoveHandler(PointerMovedEvent, HandlePointerMoved);
                _inputTopLevel.RemoveHandler(PointerReleasedEvent, HandlePointerReleased);
                RemoveHandler(PointerCaptureLostEvent, HandlePointerCaptureLost);
            }

            _inputTopLevel = null;
        }

        _swipeNavigationHandler.Reset();
        _longPressToolTipHandler.Reset();
    }

    public async Task<bool> HandleBackRequestAsync()
    {
        _longPressToolTipHandler.DismissOpenToolTip();

        if (TryDismissOpenFlyout())
            return true;

        return await _keyboardHandler.HandleBackRequestCoreAsync();
    }

    private bool TryDismissOpenFlyout()
    {
        foreach (var descendant in this.GetVisualDescendants())
        {
            if (descendant is not Button button || button.Flyout?.IsOpen != true)
                continue;

            button.Flyout.Hide();
            return true;
        }

        return false;
    }

    private async void HandleTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        _longPressToolTipHandler.DismissOpenToolTip();
        await _keyboardHandler.HandleTopLevelKeyDownAsync(e);
    }

    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        _longPressToolTipHandler.DismissOpenToolTip();
        await _keyboardHandler.HandleKeyDownAsync(e);
    }

    private async void HandleCopyingToClipboard(object? sender, RoutedEventArgs e) =>
        await _keyboardHandler.HandleCopyingToClipboardAsync(e);

    private async void HandleCuttingToClipboard(object? sender, RoutedEventArgs e) =>
        await _keyboardHandler.HandleCuttingToClipboardAsync(e);

    private void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _longPressToolTipHandler.HandlePointerPressed(e);
        _tapOutsideKeyboardDismissHandler.HandlePointerPressed(e);
        _swipeNavigationHandler.HandlePointerPressed(e);
    }

    private void HandlePointerMoved(object? sender, PointerEventArgs e)
    {
        _longPressToolTipHandler.HandlePointerMoved(e);
        if (e.Handled)
            return;

        _swipeNavigationHandler.HandlePointerMoved(e);
    }

    private void HandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _longPressToolTipHandler.HandlePointerReleased(e);
        if (e.Handled)
        {
            _swipeNavigationHandler.Reset();
            return;
        }

        _swipeNavigationHandler.HandlePointerReleased(e);
    }

    private void HandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _longPressToolTipHandler.HandlePointerCaptureLost(e);
        _swipeNavigationHandler.HandlePointerCaptureLost(e);
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TryDismissOpenFlyout();
        HideAccountMenuFlyout();
        SetActiveTopLevelForUiServices(null);
        DetachTopLevelInputHandlers();
        DetachObservedViewModel();
        AttachedToVisualTree -= HandleAttachedToVisualTree;
        DetachedFromVisualTree -= HandleDetachedFromVisualTree;
        DataContextChanged -= HandleDataContextChanged;
        RemoveHandler(KeyDownEvent, HandleKeyDown);
        RemoveHandler(TextBox.CopyingToClipboardEvent, HandleCopyingToClipboard);
        RemoveHandler(TextBox.CuttingToClipboardEvent, HandleCuttingToClipboard);
        DataContext = null;
    }

}
