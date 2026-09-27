using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal sealed class MainViewLongPressToolTipHandler
{
    private const int LongPressDelayMilliseconds = 650;
    private const double CancelDistance = 12;

    private readonly MainView _view;
    private CancellationTokenSource? _longPressDelay;
    private Button? _pressedButton;
    private Button? _openToolTipButton;
    private IPointer? _trackedPointer;
    private Point _pressPosition;
    private bool _longPressActivated;

    public MainViewLongPressToolTipHandler(MainView view)
    {
        _view = view;
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        DismissOpenToolTip();
        CancelPendingLongPress();
        ResetTrackedPress();

        if (!OperatingSystem.IsAndroid() || e.Handled || !IsPrimaryTouchOrPenPress(e))
            return;

        var button = FindButtonWithToolTip(e.Source);
        if (button is null
            || (!button.IsEffectivelyEnabled && !ToolTip.GetShowOnDisabled(button)))
        {
            return;
        }

        _pressedButton = button;
        _trackedPointer = e.Pointer;
        _pressPosition = e.GetPosition(_view);

        var delay = new CancellationTokenSource();
        _longPressDelay = delay;
        _ = OpenToolTipAfterDelayAsync(button, delay.Token);
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!IsTrackedPointer(e))
            return;

        var movement = e.GetPosition(_view) - _pressPosition;
        if (Math.Abs(movement.X) <= CancelDistance && Math.Abs(movement.Y) <= CancelDistance)
            return;

        CancelPendingLongPress();

        if (_longPressActivated)
        {
            DismissOpenToolTip();
            e.PreventGestureRecognition();
            e.Handled = true;
        }
        else
        {
            ResetTrackedPress();
        }
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (!IsTrackedPointer(e))
            return;

        var shouldSuppressTap = _longPressActivated;
        CancelPendingLongPress();
        ResetTrackedPress();

        if (!shouldSuppressTap)
            return;

        e.PreventGestureRecognition();
        e.Handled = true;
    }

    public void HandlePointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (!Equals(_trackedPointer, e.Pointer))
            return;

        CancelPendingLongPress();
        ResetTrackedPress();
    }

    public void DismissOpenToolTip()
    {
        if (_openToolTipButton is not null)
            ToolTip.SetIsOpen(_openToolTipButton, false);

        _openToolTipButton = null;
    }

    public void Reset()
    {
        CancelPendingLongPress();
        ResetTrackedPress();
        DismissOpenToolTip();
    }

    private async Task OpenToolTipAfterDelayAsync(Button button, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LongPressDelayMilliseconds, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => OpenTrackedToolTip(button, cancellationToken),
                DispatcherPriority.Input);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenTrackedToolTip(Button button, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_pressedButton, button))
            return;

        ToolTip.SetIsOpen(button, true);
        _openToolTipButton = button;
        _longPressActivated = true;
    }

    private void CancelPendingLongPress()
    {
        if (_longPressDelay is null)
            return;

        _longPressDelay.Cancel();
        _longPressDelay.Dispose();
        _longPressDelay = null;
    }

    private void ResetTrackedPress()
    {
        _pressedButton = null;
        _trackedPointer = null;
        _pressPosition = default;
        _longPressActivated = false;
    }

    private bool IsTrackedPointer(PointerEventArgs e) =>
        Equals(_trackedPointer, e.Pointer);

    private bool IsPrimaryTouchOrPenPress(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen))
            return false;

        return e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed;
    }

    private static Button? FindButtonWithToolTip(object? source)
    {
        if (source is not Control sourceControl)
            return null;

        var button = sourceControl as Button ?? sourceControl.FindAncestorOfType<Button>();
        if (button is null)
            return null;

        var tip = ToolTip.GetTip(button);
        return tip switch
        {
            null => null,
            string text when string.IsNullOrWhiteSpace(text) => null,
            _ => button
        };
    }
}
