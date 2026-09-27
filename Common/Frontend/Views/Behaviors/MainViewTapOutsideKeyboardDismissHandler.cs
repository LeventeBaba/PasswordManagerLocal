using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal sealed class MainViewTapOutsideKeyboardDismissHandler
{
    private readonly MainView _view;

    public MainViewTapOutsideKeyboardDismissHandler(MainView view)
    {
        _view = view;
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || IsInteractiveSource(e.Source))
            return;

        var topLevel = TopLevel.GetTopLevel(_view);
        var focusManager = topLevel?.FocusManager;
        if (!IsTextInputFocused(focusManager?.GetFocusedElement()))
            return;

        focusManager!.Focus(null);
        _view.PlatformServices?.Keyboard.Hide();
    }

    private static bool IsTextInputFocused(IInputElement? focusedElement)
    {
        if (focusedElement is TextBox)
            return true;

        return focusedElement is Control focusedControl
            && focusedControl.FindAncestorOfType<TextBox>() is not null;
    }

    private static bool IsInteractiveSource(object? source)
    {
        if (source is not Control sourceControl)
            return false;

        return IsWithin<TextBox>(sourceControl)
            || IsWithin<Button>(sourceControl)
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
