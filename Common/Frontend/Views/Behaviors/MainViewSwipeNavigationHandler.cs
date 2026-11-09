using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Diagnostics;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal sealed class MainViewSwipeNavigationHandler
{
    private const double MinimumFlickDistance = 24;
    private const double FlickVelocityThreshold = 650;
    private const double VelocitySmoothingFactor = 0.35;

    private readonly MainView _view;
    private Point? _startPoint;
    private Point _lastPoint;
    private IPointer? _trackedPointer;
    private IPointer? _nativeSwipeSuppressedPointer;
    private Carousel? _nativeSwipeSuppressedCarousel;
    private long _lastVelocityTimestamp;
    private double _horizontalVelocity;
    private bool _isHorizontalSwipe;
    private bool _hasCapturedPointer;
    private bool _trackingCancelled;

    public MainViewSwipeNavigationHandler(MainView view)
    {
        _view = view;
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        // A second contact must not steal/reset an active primary swipe.
        if (_trackedPointer is not null || _nativeSwipeSuppressedPointer is not null)
            return;

        Reset();
        if (TrySuppressNativeSwipeForExcludedControl(e))
            return;

        if (!CanStartTracking(e))
            return;

        var point = e.GetCurrentPoint(_view);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _trackedPointer = e.Pointer;
        _startPoint = point.Position;
        _lastPoint = point.Position;
        _lastVelocityTimestamp = Stopwatch.GetTimestamp();
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!CanContinueTracking(e))
            return;

        var currentPoint = e.GetPosition(_view);
        var movement = currentPoint - _startPoint!.Value;

        if (!_isHorizontalSwipe && !TryLockDirection(movement, e))
            return;

        UpdateVelocity(currentPoint);
        e.Handled = true;
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (Equals(_nativeSwipeSuppressedPointer, e.Pointer))
        {
            RestoreNativeSwipeIfSuppressed();
            return;
        }

        if (!CanContinueTracking(e))
        {
            Reset();
            return;
        }

        if (!_isHorizontalSwipe)
        {
            Reset();
            return;
        }

        var movement = e.GetPosition(_view) - _startPoint!.Value;
        var shouldNavigate = ShouldNavigate(movement.X);
        var navigateForward = movement.X < 0;

        e.Handled = true;
        ReleasePointerCapture();
        ResetState();

        if (shouldNavigate)
            Navigate(navigateForward);
    }

    public void HandlePointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (Equals(_nativeSwipeSuppressedPointer, e.Pointer))
            RestoreNativeSwipeIfSuppressed();

        if (Equals(_trackedPointer, e.Pointer))
            ResetState();
    }

    public void Reset()
    {
        RestoreNativeSwipeIfSuppressed();
        ReleasePointerCapture();
        ResetState();
    }

    private bool TrySuppressNativeSwipeForExcludedControl(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen) ||
            _view.DataContext is not MainViewModel { IsAnimatedMobileMainPageSwipeEnabled: true } ||
            !IsPointerInsideView(e) ||
            !IsExcludedInputSource(e.Source) ||
            e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed == false)
        {
            return false;
        }

        var carousel = FindMobileMainCarousel(e.Source);
        if (carousel is null)
            return false;

        _nativeSwipeSuppressedPointer = e.Pointer;
        _nativeSwipeSuppressedCarousel = carousel;
        carousel.SetCurrentValue(Carousel.IsSwipeEnabledProperty, false);
        return true;
    }

    private void RestoreNativeSwipeIfSuppressed()
    {
        if (_nativeSwipeSuppressedCarousel is { } carousel)
        {
            var shouldEnable = _view.DataContext is MainViewModel
            {
                IsAnimatedMobileMainPageSwipeEnabled: true
            };
            carousel.SetCurrentValue(Carousel.IsSwipeEnabledProperty, shouldEnable);
        }

        _nativeSwipeSuppressedPointer = null;
        _nativeSwipeSuppressedCarousel = null;
    }

    private bool CanStartTracking(PointerPressedEventArgs e) =>
        e.Pointer.Type is PointerType.Touch or PointerType.Pen
        && IsNavigationEnabled()
        && IsPointerInsideView(e)
        && IsPointerInsideMobileMainPager(e.Source)
        && !IsExcludedInputSource(e.Source);

    private bool CanContinueTracking(PointerEventArgs e) =>
        _startPoint is not null
        && !_trackingCancelled
        && Equals(_trackedPointer, e.Pointer)
        && IsNavigationEnabled();

    private bool TryLockDirection(Vector movement, PointerEventArgs e)
    {
        switch (TouchGestureIntentClassifier.Classify(movement))
        {
            case TouchGestureIntent.Horizontal:
                BeginHorizontalSwipe(e);
                return true;
            case TouchGestureIntent.Vertical:
                _trackingCancelled = true;
                break;
        }

        return false;
    }

    private void BeginHorizontalSwipe(PointerEventArgs e)
    {
        _isHorizontalSwipe = true;
        _hasCapturedPointer = true;
        e.Pointer.Capture(_view);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void UpdateVelocity(Point currentPoint)
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (_lastVelocityTimestamp != 0)
        {
            var elapsedSeconds = (double)(timestamp - _lastVelocityTimestamp) / Stopwatch.Frequency;
            var deltaX = currentPoint.X - _lastPoint.X;
            if (elapsedSeconds is > 0 and <= 0.12 && Math.Abs(deltaX) >= 0.5)
            {
                var instantVelocity = deltaX / elapsedSeconds;
                _horizontalVelocity = _horizontalVelocity == 0
                    ? instantVelocity
                    : (_horizontalVelocity * (1 - VelocitySmoothingFactor))
                      + (instantVelocity * VelocitySmoothingFactor);
            }
        }

        _lastPoint = currentPoint;
        _lastVelocityTimestamp = timestamp;
    }

    private bool ShouldNavigate(double horizontalDistance)
    {
        var distanceThreshold = Math.Clamp(_view.Bounds.Width * 0.16, 52, 88);
        var completedByDistance = Math.Abs(horizontalDistance) >= distanceThreshold;
        var completedByFlick = Math.Abs(horizontalDistance) >= MinimumFlickDistance
            && Math.Abs(_horizontalVelocity) >= FlickVelocityThreshold
            && Math.Sign(horizontalDistance) == Math.Sign(_horizontalVelocity);

        return completedByDistance || completedByFlick;
    }

    private void Navigate(bool forward)
    {
        if (_view.DataContext is not MainViewModel viewModel)
            return;

        if (forward)
            viewModel.NavigateToNextMainPage();
        else
            viewModel.NavigateToPreviousMainPage();
    }

    private void ReleasePointerCapture()
    {
        if (!_hasCapturedPointer || _trackedPointer is null)
            return;

        _hasCapturedPointer = false;
        _trackedPointer.Capture(null);
    }

    private void ResetState()
    {
        _startPoint = null;
        _trackedPointer = null;
        _lastPoint = default;
        _lastVelocityTimestamp = 0;
        _horizontalVelocity = 0;
        _isHorizontalSwipe = false;
        _hasCapturedPointer = false;
        _trackingCancelled = false;
    }

    private bool IsPointerInsideView(PointerEventArgs e) =>
        new Rect(0, 0, _view.Bounds.Width, _view.Bounds.Height).Contains(e.GetPosition(_view));

    private bool IsNavigationEnabled() =>
        _view.DataContext is MainViewModel { IsCustomMobileMainPageSwipeEnabled: true };

    private bool IsPointerInsideMobileMainPager(object? source) =>
        FindMobileMainCarousel(source) is not null;

    private Carousel? FindMobileMainCarousel(object? source)
    {
        if (source is not Control sourceControl)
            return null;

        var carousel = _view.FindControl<Carousel>("MobileMainCarousel");
        if (carousel is null)
            return null;

        return ReferenceEquals(sourceControl, carousel) || sourceControl.FindAncestorOfType<Carousel>() == carousel
            ? carousel
            : null;
    }

    private static bool IsExcludedInputSource(object? source)
    {
        if (source is not Control sourceControl)
            return false;

        // Ordinary release-mode buttons deliberately remain swipe-capable. Once the
        // horizontal gesture wins, pointer capture cancels the pending tap. Controls
        // that own text selection or horizontal/direct manipulation keep priority.
        return TextBoxClipboardHandler.FindSourceTextBox(sourceControl) is not null
            || IsWithin<ToggleButton>(sourceControl)
            || IsWithin<ComboBox>(sourceControl)
            || IsWithin<Slider>(sourceControl)
            || IsWithin<ScrollBar>(sourceControl)
            || IsWithin<TabItem>(sourceControl)
            || IsWithin<MenuItem>(sourceControl);
    }

    private static bool IsWithin<TControl>(Control sourceControl)
        where TControl : Control =>
        sourceControl is TControl || sourceControl.FindAncestorOfType<TControl>() is not null;
}
