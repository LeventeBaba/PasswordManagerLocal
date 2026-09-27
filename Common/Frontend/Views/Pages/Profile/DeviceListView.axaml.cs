using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Profile;

public partial class DeviceListView : UserControl
{
    private const string HoveredClass = "hovered";
    private const string InteractiveListItemClass = "interactiveListItem";
    private const double MaximumTapMovement = 10;

    private static readonly IBrush NormalBackground = new SolidColorBrush(Color.FromArgb(0x08, 0x80, 0x80, 0x80));
    private static readonly IBrush NormalBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private static readonly IBrush HoverBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly IBrush HoverBorderBrush = new SolidColorBrush(Color.FromArgb(0xDD, 0x2D, 0x6A, 0xE3));

    private Border? _pressedItem;
    private IPointer? _pressedPointer;
    private Point _pressedPoint;

    public DeviceListView()
    {
        InitializeComponent();
    }

    private void InteractiveListItem_PointerEntered(object? sender, PointerEventArgs e)
    {
        SetHoverVisuals(sender, isHovered: true);
    }

    private void InteractiveListItem_PointerExited(object? sender, PointerEventArgs e)
    {
        SetHoverVisuals(sender, isHovered: false);
        ResetPressedItem();
    }

    private void InteractiveListItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPressedItem();

        if (IsNestedActionSource(e.Source))
        {
            return;
        }

        Border? item = FindInteractiveListItem(sender) ?? FindInteractiveListItem(e.Source);
        if (item is null)
        {
            return;
        }

        _pressedItem = item;
        _pressedPointer = e.Pointer;
        _pressedPoint = e.GetPosition(item);
    }

    private void InteractiveListItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Border? pressedItem = _pressedItem;
        IPointer? pressedPointer = _pressedPointer;
        Point pressedPoint = _pressedPoint;
        ResetPressedItem();

        if (pressedItem is null || !Equals(pressedPointer, e.Pointer) || IsNestedActionSource(e.Source))
        {
            return;
        }

        Border? releasedItem = FindInteractiveListItem(sender) ?? FindInteractiveListItem(e.Source);
        if (!ReferenceEquals(pressedItem, releasedItem))
        {
            return;
        }

        Vector movement = e.GetPosition(pressedItem) - pressedPoint;
        if (Math.Abs(movement.X) > MaximumTapMovement || Math.Abs(movement.Y) > MaximumTapMovement)
        {
            return;
        }

        ExecuteViewCommand(pressedItem);
        e.Handled = true;
    }

    private void ResetPressedItem()
    {
        _pressedItem = null;
        _pressedPointer = null;
        _pressedPoint = default;
    }

    private static void ExecuteViewCommand(Border item)
    {
        if (item.DataContext is not DeviceItemViewModel device)
        {
            return;
        }

        ICommand command = device.ViewCommand;
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
        control is Button or ToggleSwitch;

    private static IEnumerable<Control> EnumerateSelfAndAncestors(Control control)
    {
        yield return control;

        foreach (Control ancestor in control.GetVisualAncestors().OfType<Control>())
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
        Border? item = FindInteractiveListItem(sender);
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

            item.Background = HoverBackground;
            item.BorderBrush = HoverBorderBrush;
            return;
        }

        item.Classes.Remove(HoveredClass);
        item.Background = NormalBackground;
        item.BorderBrush = NormalBorderBrush;
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
