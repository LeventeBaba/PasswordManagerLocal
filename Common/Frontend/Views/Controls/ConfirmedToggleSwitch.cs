using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

/// <summary>
/// Requests a change through Command while displaying only the confirmed model
/// value. Suitable for settings requiring confirmation or an asynchronous save.
/// </summary>
public sealed class ConfirmedToggleSwitch : ToggleSwitch
{
    public static readonly StyledProperty<bool> ConfirmedIsCheckedProperty =
        AvaloniaProperty.Register<ConfirmedToggleSwitch, bool>(
            nameof(ConfirmedIsChecked), defaultBindingMode: BindingMode.OneWay);

    private Panel? _knobsPanel;

    public bool ConfirmedIsChecked
    {
        get => GetValue(ConfirmedIsCheckedProperty);
        set => SetValue(ConfirmedIsCheckedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ToggleSwitch);

    // Click, keyboard, and accessibility activation still run Button.Command.
    // They must not move the switch before that command has committed the change.
    protected override void Toggle() { }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ConfirmedIsCheckedProperty)
            SetCurrentValue(IsCheckedProperty, ConfirmedIsChecked);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_knobsPanel is not null)
            _knobsPanel.RemoveHandler(PointerReleasedEvent, HandleKnobReleased);

        base.OnApplyTemplate(e);
        _knobsPanel = e.NameScope.Find<Panel>("PART_MovingKnobs");
        // The native drag handler changes IsChecked directly and handles release,
        // bypassing Toggle() and Command. Run after it, including handled events.
        _knobsPanel?.AddHandler(PointerReleasedEvent, HandleKnobReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void HandleKnobReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsChecked == ConfirmedIsChecked)
            return;

        SetCurrentValue(IsCheckedProperty, ConfirmedIsChecked);
        e.Handled = true;
        OnClick();
    }
}
