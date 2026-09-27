using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Frontend.Views.Behaviors;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordListPaneView : UserControl
{
    private const string HoveredClass = "hovered";
    private const string InteractiveListItemClass = "interactiveListItem";

    private readonly MultiSelectionListItemPointerHandler<PasswordItemViewModel> _multiSelectionPointerHandler;

    public PasswordListPaneView()
    {
        InitializeComponent();
        _multiSelectionPointerHandler = new MultiSelectionListItemPointerHandler<PasswordItemViewModel>(
            this,
            BeginMultiSelection);

        RegisterAndroidMultiSelectionInputHandlers();
    }

    private void RegisterAndroidMultiSelectionInputHandlers()
    {
        if (!OperatingSystem.IsAndroid())
        {
            return;
        }

        AddHandler(
            PointerPressedEvent,
            AndroidInteractiveListItem_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerMovedEvent,
            AndroidInteractiveListItem_PointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            AndroidInteractiveListItem_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void AndroidInteractiveListItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsNestedActionSource(e.Source))
        {
            return;
        }

        var item = FindInteractiveListItem(e.Source);
        if (item is not null)
        {
            _multiSelectionPointerHandler.HandlePointerPressed(item, e);
        }
    }

    private void AndroidInteractiveListItem_PointerMoved(object? sender, PointerEventArgs e) =>
        _multiSelectionPointerHandler.HandlePointerMoved(e);

    private void AndroidInteractiveListItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsNestedActionSource(e.Source))
        {
            _multiSelectionPointerHandler.CancelPointerTracking();
            return;
        }

        var item = FindInteractiveListItem(e.Source);
        if (item is null || _multiSelectionPointerHandler.HandlePointerReleased(item, e))
        {
            return;
        }

        // The row's main content is a Button. Let a normal tap on that Button execute
        // its own ViewCommand; taps on the rest of the row still open the password here.
        if (IsListItemContentButtonSource(e.Source))
        {
            return;
        }

        ExecuteViewCommand(item);
        e.Handled = true;
    }

    private void InteractiveListItem_PointerEntered(object? sender, PointerEventArgs e) =>
        SetHoverVisuals(sender, isHovered: true);

    private void InteractiveListItem_PointerExited(object? sender, PointerEventArgs e)
    {
        SetHoverVisuals(sender, isHovered: false);
        _multiSelectionPointerHandler.CancelPointerTracking();
    }

    private void InteractiveListItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        if (IsNestedActionSource(e.Source))
        {
            return;
        }

        var item = FindInteractiveListItem(sender) ?? FindInteractiveListItem(e.Source);
        if (item is not null)
        {
            _multiSelectionPointerHandler.HandlePointerPressed(item, e);
        }
    }

    private void InteractiveListItem_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!OperatingSystem.IsAndroid())
        {
            _multiSelectionPointerHandler.HandlePointerMoved(e);
        }
    }

    private void InteractiveListItem_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!OperatingSystem.IsAndroid())
        {
            _multiSelectionPointerHandler.HandlePointerCaptureLost(e);
        }
    }

    private void InteractiveListItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        if (IsNestedActionSource(e.Source))
        {
            _multiSelectionPointerHandler.CancelPointerTracking();
            return;
        }

        var item = FindInteractiveListItem(sender) ?? FindInteractiveListItem(e.Source);
        if (item is null || _multiSelectionPointerHandler.HandlePointerReleased(item, e))
        {
            return;
        }

        if (IsListItemContentButtonSource(e.Source))
        {
            return;
        }

        ExecuteViewCommand(item);
        e.Handled = true;
    }

    private void BeginMultiSelection(PasswordItemViewModel password)
    {
        if (DataContext is PasswordsViewModel viewModel)
        {
            viewModel.BeginPasswordMultiSelection(password);
        }
    }

    private static void ExecuteViewCommand(Border item)
    {
        if (item.DataContext is not PasswordItemViewModel password)
        {
            return;
        }

        ICommand command = password.ViewCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private static bool IsNestedActionSource(object? source)
    {
        if (source is not Control control)
        {
            return false;
        }

        return EnumerateSelfAndAncestors(control).Any(IsNestedActionControl);
    }

    private static bool IsNestedActionControl(Control control)
    {
        if (control is ToggleSwitch or CheckBox)
        {
            return true;
        }

        return control is Button button && !button.Classes.Contains("listItemContentButton");
    }

    private static bool IsListItemContentButtonSource(object? source)
    {
        if (source is not Control control)
        {
            return false;
        }

        return EnumerateSelfAndAncestors(control)
            .OfType<Button>()
            .Any(button => button.Classes.Contains("listItemContentButton"));
    }

    private static IEnumerable<Control> EnumerateSelfAndAncestors(Control control)
    {
        yield return control;

        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor is Border border && border.Classes.Contains(InteractiveListItemClass))
            {
                yield break;
            }

            yield return ancestor;
        }
    }

    private static void SetHoverVisuals(object? sender, bool isHovered)
    {
        var item = FindInteractiveListItem(sender);
        if (item is null)
        {
            return;
        }

        if (isHovered)
        {
            if (!item.Classes.Contains(HoveredClass))
            {
                item.Classes.Add(HoveredClass);
            }

            return;
        }

        item.Classes.Remove(HoveredClass);
    }

    private static Border? FindInteractiveListItem(object? sender)
    {
        if (sender is Border border && border.Classes.Contains(InteractiveListItemClass))
        {
            return border;
        }

        if (sender is not Control control)
        {
            return null;
        }

        return control
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(candidate => candidate.Classes.Contains(InteractiveListItemClass));
    }
}
