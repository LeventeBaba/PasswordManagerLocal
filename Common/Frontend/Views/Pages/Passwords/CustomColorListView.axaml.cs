using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Frontend.Views.Behaviors;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class CustomColorListView : UserControl
{
    private const string HoveredClass = "hovered";
    private const string InteractiveListItemClass = "interactiveListItem";

    private readonly MultiSelectionListItemPointerHandler<CustomColorItemViewModel> _multiSelectionPointerHandler;

    public CustomColorListView()
    {
        InitializeComponent();
        _multiSelectionPointerHandler = new MultiSelectionListItemPointerHandler<CustomColorItemViewModel>(
            this,
            BeginMultiSelection);
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

    private void InteractiveListItem_PointerMoved(object? sender, PointerEventArgs e) =>
        _multiSelectionPointerHandler.HandlePointerMoved(e);

    private void InteractiveListItem_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _multiSelectionPointerHandler.HandlePointerCaptureLost(e);

    private void InteractiveListItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
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

        ExecuteEditCommand(item);
        e.Handled = true;
    }

    private void BeginMultiSelection(CustomColorItemViewModel customColor)
    {
        if (DataContext is PasswordsViewModel viewModel)
        {
            viewModel.BeginCustomColorMultiSelection(customColor);
        }
    }

    private static void ExecuteEditCommand(Border item)
    {
        if (item.DataContext is not CustomColorItemViewModel customColor)
        {
            return;
        }

        ICommand command = customColor.EditCommand;
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

    private static bool IsNestedActionControl(Control control) =>
        control is Button or ToggleSwitch or CheckBox;

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
