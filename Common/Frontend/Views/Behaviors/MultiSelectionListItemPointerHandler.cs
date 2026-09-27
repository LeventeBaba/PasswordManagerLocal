using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal sealed class MultiSelectionListItemPointerHandler<TItem>
    where TItem : MultiSelectableListItemViewModel
{
    private const int LongPressDelayMilliseconds = 650;
    private const double CancelDistance = 12;

    private readonly Control _owner;
    private readonly Action<TItem> _beginSelection;

    private TopLevel? _inputTopLevel;
    private CancellationTokenSource? _longPressDelay;
    private Border? _pressedItem;
    private IPointer? _trackedPointer;
    private IPointer? _suppressedReleasePointer;
    private Point _pressPosition;
    private bool _longPressActivated;

    public MultiSelectionListItemPointerHandler(Control owner, Action<TItem> beginSelection)
    {
        _owner = owner;
        _beginSelection = beginSelection;

        if (OperatingSystem.IsAndroid())
        {
            _owner.AttachedToVisualTree += HandleAttachedToVisualTree;
            _owner.DetachedFromVisualTree += HandleDetachedFromVisualTree;
            AttachTopLevelInputHandlers();
        }
    }

    public void HandlePointerPressed(Border item, PointerPressedEventArgs e)
    {
        CancelPendingLongPress();
        ResetTrackedPress();
        _suppressedReleasePointer = null;

        if (e.Handled || item.DataContext is not TItem itemViewModel)
        {
            return;
        }

        var properties = e.GetCurrentPoint(item).Properties;
        if (OperatingSystem.IsWindows() && properties.IsRightButtonPressed)
        {
            ActivateSelection(itemViewModel);
            _suppressedReleasePointer = e.Pointer;
            e.PreventGestureRecognition();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedItem = item;
        _trackedPointer = e.Pointer;

        // The list content moves inside its ScrollViewer during touch panning. Measure
        // against the stable top-level coordinate system so that movement remains visible.
        _pressPosition = e.GetPosition(null);

        if (OperatingSystem.IsAndroid() && e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            var delay = new CancellationTokenSource();
            _longPressDelay = delay;
            _ = ActivateLongPressAfterDelayAsync(item, itemViewModel, delay.Token);
        }
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!Equals(_trackedPointer, e.Pointer))
        {
            return;
        }

        var movement = e.GetPosition(null) - _pressPosition;
        if (Math.Abs(movement.X) <= CancelDistance && Math.Abs(movement.Y) <= CancelDistance)
        {
            return;
        }

        CancelPendingLongPress();

        if (_longPressActivated)
        {
            e.PreventGestureRecognition();
            e.Handled = true;
            return;
        }

        ResetTrackedPress();
    }

    public bool HandlePointerReleased(Border releasedItem, PointerReleasedEventArgs e)
    {
        if (Equals(_suppressedReleasePointer, e.Pointer))
        {
            _suppressedReleasePointer = null;
            e.PreventGestureRecognition();
            e.Handled = true;
            return true;
        }

        var pressedItem = _pressedItem;
        var trackedPointer = _trackedPointer;
        var pressPosition = _pressPosition;
        var longPressActivated = _longPressActivated;

        CancelPendingLongPress();
        ResetTrackedPress();

        if (pressedItem is null || !Equals(trackedPointer, e.Pointer) || !ReferenceEquals(pressedItem, releasedItem))
        {
            return true;
        }

        var movement = e.GetPosition(null) - pressPosition;
        if (Math.Abs(movement.X) > CancelDistance || Math.Abs(movement.Y) > CancelDistance)
        {
            return true;
        }

        if (longPressActivated)
        {
            e.PreventGestureRecognition();
            e.Handled = true;
            return true;
        }

        if (releasedItem.DataContext is TItem { IsSelectionModeActive: true } itemViewModel)
        {
            itemViewModel.IsSelected = !itemViewModel.IsSelected;
            e.PreventGestureRecognition();
            e.Handled = true;
            return true;
        }

        return false;
    }

    public void HandlePointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (!Equals(_trackedPointer, e.Pointer))
        {
            return;
        }

        CancelPointerTracking();
    }

    public void CancelPointerTracking()
    {
        CancelPendingLongPress();
        ResetTrackedPress();
        _suppressedReleasePointer = null;
    }

    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        AttachTopLevelInputHandlers();

    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachTopLevelInputHandlers();
        CancelPointerTracking();
    }

    private void AttachTopLevelInputHandlers()
    {
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (ReferenceEquals(_inputTopLevel, topLevel))
        {
            return;
        }

        DetachTopLevelInputHandlers();

        if (topLevel is null)
        {
            return;
        }

        _inputTopLevel = topLevel;

        // ScrollViewer captures the pointer after recognizing a pan, so item-level
        // handlers may no longer receive later move or release events. Observe both
        // raw movement and recognized scrolling at the top level.
        topLevel.AddHandler(
            InputElement.PointerMovedEvent,
            HandleTopLevelPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        topLevel.AddHandler(
            InputElement.ScrollGestureEvent,
            HandleTopLevelScrollGesture,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void DetachTopLevelInputHandlers()
    {
        if (_inputTopLevel is null)
        {
            return;
        }

        _inputTopLevel.RemoveHandler(InputElement.PointerMovedEvent, HandleTopLevelPointerMoved);
        _inputTopLevel.RemoveHandler(InputElement.ScrollGestureEvent, HandleTopLevelScrollGesture);
        _inputTopLevel = null;
    }

    private void HandleTopLevelPointerMoved(object? sender, PointerEventArgs e) =>
        HandlePointerMoved(e);

    private void HandleTopLevelScrollGesture(object? sender, ScrollGestureEventArgs e) =>
        CancelPointerTracking();

    private async Task ActivateLongPressAfterDelayAsync(
        Border item,
        TItem itemViewModel,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LongPressDelayMilliseconds, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => ActivateTrackedLongPress(item, itemViewModel, cancellationToken),
                DispatcherPriority.Input);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ActivateTrackedLongPress(
        Border item,
        TItem itemViewModel,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_pressedItem, item))
        {
            return;
        }

        ActivateSelection(itemViewModel);
        _longPressActivated = true;
    }

    private void ActivateSelection(TItem itemViewModel)
    {
        if (itemViewModel.IsSelectionModeActive)
        {
            itemViewModel.IsSelected = !itemViewModel.IsSelected;
            return;
        }

        _beginSelection(itemViewModel);
    }

    private void CancelPendingLongPress()
    {
        if (_longPressDelay is null)
        {
            return;
        }

        _longPressDelay.Cancel();
        _longPressDelay.Dispose();
        _longPressDelay = null;
    }

    private void ResetTrackedPress()
    {
        _pressedItem = null;
        _trackedPointer = null;
        _pressPosition = default;
        _longPressActivated = false;
    }
}
