using Avalonia;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

/// <summary>A single lazy content host with an optional UI-owned motion layer.</summary>
public sealed class AdaptiveTransitioningContentControl : TransitioningContentControl
{
    public static readonly StyledProperty<bool> AnimationsEnabledProperty =
        AvaloniaProperty.Register<AdaptiveTransitioningContentControl, bool>(nameof(AnimationsEnabled));

    private readonly SharedAxisPageTransition _transition = new();

    public AdaptiveTransitioningContentControl()
    {
        PageTransition = null;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        ClipToBounds = true;
        DetachedFromVisualTree += (_, _) => _transition.Cancel();
    }

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    public bool AnimationsEnabled
    {
        get => GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == AnimationsEnabledProperty)
        {
            if (!AnimationsEnabled)
                _transition.Cancel();
            PageTransition = AnimationsEnabled ? _transition : null;
        }
        if (change.Property == IsVisibleProperty && !IsVisible)
            _transition.Cancel();
        base.OnPropertyChanged(change);
    }
}
