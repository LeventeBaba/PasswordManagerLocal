using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views.Behaviors;
using PasswordManagerLocal.Common.Frontend.Views.Controls;
using System.Collections.Generic;
using System.ComponentModel;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class MainView : UserControl, IDisposable
{
    private readonly MainViewKeyboardHandler _keyboardHandler;
    private readonly MainViewSwipeNavigationHandler _swipeNavigationHandler;
    private readonly MainViewTapOutsideKeyboardDismissHandler _tapOutsideKeyboardDismissHandler;
    private readonly MainViewLongPressToolTipHandler _longPressToolTipHandler;
    private readonly PageSlide _mobileMainPageTransition;
    private readonly HashSet<Button> _accountMenuToolTipButtons = new();
    private bool _disposed;
    private WeakReference<Control>? _contextHelpTrigger;
    private IInputPane? _inputPane;
    internal FrontendPlatformServices? PlatformServices => (DataContext as MainViewModel)?.PlatformServices;
    private TopLevel? _inputTopLevel;
    private MainViewModel? _observedViewModel;

    public MainView()
    {
        InitializeComponent();
        ConfigureWindowsMainNavigationVisualStates();
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
        _longPressToolTipHandler = new MainViewLongPressToolTipHandler();
        RegisterLocalInputHandlers();
        RegisterLifecycleHandlers();
        HandleDataContextChanged(this, EventArgs.Empty);
    }

    private void ConfigureWindowsMainNavigationVisualStates()
    {
        if (!OperatingSystem.IsWindows())
            return;

        foreach (var button in DesktopMainNavigationButtons.Children.OfType<Button>())
            button.Classes.Add("windowsMainNavigation");
    }

    private void RegisterLocalInputHandlers()
    {
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(ContextHelpButton.ContextHelpRequestedEvent, HandleContextHelpRequested);
        AddHandler(
            TextBox.CopyingToClipboardEvent,
            HandleCopyingToClipboard,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble | RoutingStrategies.Direct);
        AddHandler(
            TextBox.CuttingToClipboardEvent,
            HandleCuttingToClipboard,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble | RoutingStrategies.Direct);
    }

    private void HandleContextHelpRequested(object? sender, ContextHelpRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        if (!viewModel.IsContextHelpOpen && e.Source is Control trigger)
            _contextHelpTrigger = new WeakReference<Control>(trigger);
        e.Handled = true;
        viewModel.OpenContextHelp(e.TopicId);
    }

    private void RestoreContextHelpFocus()
    {
        var trigger = _contextHelpTrigger;
        _contextHelpTrigger = null;
        var viewModel = _observedViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || viewModel is null || !ReferenceEquals(DataContext, viewModel) ||
                viewModel.IsContextHelpOpen || !viewModel.IsContextHelpInteractive)
                return;
            if (trigger is not null && trigger.TryGetTarget(out var control) && control.IsEffectivelyEnabled &&
                control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) == TopLevel.GetTopLevel(this) &&
                control.Focus(NavigationMethod.Directional))
                return;
            ApplicationSurface.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
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

        // Not every Android top-level exposes an input pane (e.g. during
        // initialization/teardown). Keep pointer handlers active either way.
        _inputPane = topLevel.InputPane;
        if (_inputPane is { } inputPane)
        {
            inputPane.StateChanged += HandleInputPaneStateChanged;
            SetPageViewportKeyboardInset(inputPane.State == InputPaneState.Open ? inputPane.OccludedRect.Height : 0);
        }

        topLevel.AddHandler(PointerPressedEvent, HandlePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerMovedEvent, HandlePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, HandlePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, HandlePointerCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
    }

    private void HandleInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || sender is not IInputPane inputPane)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // State changes can be queued while the view is detaching or when a newer
            // keyboard state has already arrived. Read the current pane state when the
            // callback runs instead of applying the possibly stale event rectangle.
            if (!ReferenceEquals(_inputPane, inputPane))
                return;

            SetPageViewportKeyboardInset(
                inputPane.State == InputPaneState.Open ? inputPane.OccludedRect.Height : 0);
        });
    }

    private void SetPageViewportKeyboardInset(double occludedHeight)
    {
        var inset = Math.Max(0, occludedHeight);
        PageViewport.Margin = new Thickness(0, 0, 0, inset);

        if (inset <= 0 || _inputTopLevel?.FocusManager.GetFocusedElement() is not Control focusedControl)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(focusedControl) == _inputTopLevel)
                focusedControl.BringIntoView();
        }, DispatcherPriority.Background);
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
        _contextHelpTrigger = null;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var loggedOut = e.PropertyName == nameof(MainViewModel.IsAuthenticated)
            && sender is MainViewModel { IsAuthenticated: false };
        if (loggedOut ||
            e.PropertyName is nameof(MainViewModel.CurrentUserDisplayName) or nameof(MainViewModel.CurrentUserFirstName))
            HideAccountMenuFlyout();

        if (e.PropertyName == nameof(MainViewModel.IsContextHelpOpen) &&
            sender is MainViewModel { IsContextHelpOpen: false })
            RestoreContextHelpFocus();

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

    private void HandleDesktopAccountMenuFlyoutOpened(object? sender, EventArgs e) =>
        SetAccountMenuChevronAngle("DesktopAccountMenuChevron", 180);

    private void HandleDesktopAccountMenuFlyoutClosed(object? sender, EventArgs e) =>
        SetAccountMenuChevronAngle("DesktopAccountMenuChevron", 0);

    private void HandleMobileAccountMenuFlyoutOpened(object? sender, EventArgs e) =>
        SetAccountMenuChevronAngle("MobileAccountMenuChevron", 180);

    private void HandleMobileAccountMenuFlyoutClosed(object? sender, EventArgs e) =>
        SetAccountMenuChevronAngle("MobileAccountMenuChevron", 0);

    private void SetAccountMenuChevronAngle(string iconName, double angle)
    {
        if (this.FindControl<PathIcon>(iconName)?.RenderTransform is RotateTransform rotation)
            rotation.Angle = angle;
    }

    private void HandleAccountMenuToolTipButtonAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || sender is not Button button ||
            !_accountMenuToolTipButtons.Add(button))
            return;

        // An overlay popup belongs to the main TopLevel visually, but routes input
        // through its logical Popup parent instead of its visual ancestors. The
        // TopLevel need not be on that route. Observe the button before its click.
        button.AddHandler(
            PointerPressedEvent,
            HandleAccountMenuToolTipPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        button.AddHandler(
            PointerMovedEvent,
            HandleAccountMenuToolTipPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        button.AddHandler(
            PointerReleasedEvent,
            HandleAccountMenuToolTipPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void HandleAccountMenuToolTipButtonDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button)
            DetachAccountMenuToolTipButton(button);
    }

    private void HandleAccountMenuToolTipPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _longPressToolTipHandler.HandlePointerCaptureLost(e);

    private void HandleAccountMenuToolTipPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _longPressToolTipHandler.HandlePointerPressed(e);

    private void HandleAccountMenuToolTipPointerMoved(object? sender, PointerEventArgs e) =>
        _longPressToolTipHandler.HandlePointerMoved(e);

    private void HandleAccountMenuToolTipPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        _longPressToolTipHandler.HandlePointerReleased(e);

    private void DetachAccountMenuToolTipButton(Button button)
    {
        if (!_accountMenuToolTipButtons.Remove(button))
            return;

        button.RemoveHandler(
            PointerPressedEvent,
            HandleAccountMenuToolTipPointerPressed);
        button.RemoveHandler(
            PointerMovedEvent,
            HandleAccountMenuToolTipPointerMoved);
        button.RemoveHandler(
            PointerReleasedEvent,
            HandleAccountMenuToolTipPointerReleased);
        _longPressToolTipHandler.HandleButtonDetached(button);
    }

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
        if (_inputPane is not null)
        {
            _inputPane.StateChanged -= HandleInputPaneStateChanged;
            _inputPane = null;
            PageViewport.Margin = default;
        }

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
        foreach (var button in _accountMenuToolTipButtons.ToArray())
            DetachAccountMenuToolTipButton(button);
        SetActiveTopLevelForUiServices(null);
        DetachTopLevelInputHandlers();
        DetachObservedViewModel();
        AttachedToVisualTree -= HandleAttachedToVisualTree;
        DetachedFromVisualTree -= HandleDetachedFromVisualTree;
        DataContextChanged -= HandleDataContextChanged;
        RemoveHandler(KeyDownEvent, HandleKeyDown);
        RemoveHandler(ContextHelpButton.ContextHelpRequestedEvent, HandleContextHelpRequested);
        RemoveHandler(TextBox.CopyingToClipboardEvent, HandleCopyingToClipboard);
        RemoveHandler(TextBox.CuttingToClipboardEvent, HandleCuttingToClipboard);
        DataContext = null;
    }

}
