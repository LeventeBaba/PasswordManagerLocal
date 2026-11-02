using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordTagPickerView : UserControl
{
    private PasswordTagPickerViewModel? _picker;
    private bool _isRestoring;
    private int _restoreRequest;
    private int _focusedTagIndex;
    private int _focusRequest;

    public PasswordTagPickerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindPicker();
        AttachedToVisualTree += (_, _) => BindPicker();
        DetachedFromVisualTree += (_, _) => UnbindPicker();
        TagListScrollViewer.ScrollChanged += (_, _) =>
        {
            if (_picker is { IsActive: true } && !_isRestoring && IsEffectivelyVisible)
                _picker.ScrollOffset = TagListScrollViewer.Offset.Y;
        };
        TagSearchTextBox.AddHandler(KeyDownEvent, HandleSearchKeyDown, RoutingStrategies.Tunnel);
        TagListItems.AddHandler(KeyDownEvent, HandleListKeyDown, RoutingStrategies.Tunnel);
        TagListItems.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is Control { DataContext: PasswordTagPickerItemViewModel tag } && _picker is { } picker)
                _focusedTagIndex = picker.Tags.IndexOf(tag);
        });
    }

    internal void PrepareForDisplay()
    {
        if (_picker is not { } picker)
            return;
        RestoreScroll(picker);
        Dispatcher.UIThread.Post(() =>
        {
            if (picker.IsActive && ReferenceEquals(_picker, picker) && IsEffectivelyVisible)
            {
                if (picker.ShowPickerDetails)
                    PickerBackButton.Focus(NavigationMethod.Directional);
                else
                    PickerDoneButton.Focus(NavigationMethod.Directional);
                picker.PlatformServices.Keyboard.Hide();
            }
        }, DispatcherPriority.Loaded);
    }

    private void BindPicker()
    {
        UnbindPicker();
        if (DataContext is not PasswordTagPickerViewModel picker)
            return;
        _picker = picker;
        picker.PropertyChanged += HandlePickerChanged;
    }

    private void UnbindPicker()
    {
        _restoreRequest++;
        _focusRequest++;
        if (_picker is not null)
            _picker.PropertyChanged -= HandlePickerChanged;
        _picker = null;
        _isRestoring = false;
    }

    private void HandlePickerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PasswordTagPickerViewModel.Tags) && _picker is { } picker)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            var removedFocusedRow = focused is Control { DataContext: PasswordTagPickerItemViewModel tag }
                && !picker.Tags.Contains(tag);
            RestoreScroll(picker);
            if (removedFocusedRow)
            {
                if (picker.Tags.Count > 0)
                    FocusTagAt(Math.Clamp(_focusedTagIndex, 0, picker.Tags.Count - 1));
                else
                    PickerDoneButton.Focus(NavigationMethod.Directional);
            }
        }
    }

    private void RestoreScroll(PasswordTagPickerViewModel picker)
    {
        var request = ++_restoreRequest;
        var offset = picker.ScrollOffset;
        _isRestoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _restoreRequest || !ReferenceEquals(_picker, picker))
                return;
            TagListScrollViewer.Offset = new Vector(0, offset);
            _isRestoring = false;
        }, DispatcherPriority.Loaded);
    }

    private void HandleSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Down))
            return;
        e.Handled = true;
        if (OperatingSystem.IsAndroid() && e.Key == Key.Enter)
        {
            PickerDoneButton.Focus(NavigationMethod.Directional);
            _picker?.PlatformServices.Keyboard.Hide();
            return;
        }
        FocusTagAt(0);
    }

    private void HandleListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || _picker is not { } picker
            || e.Source is not Control { DataContext: PasswordTagPickerItemViewModel tag })
            return;
        var index = picker.Tags.IndexOf(tag);
        if (index < 0)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            tag.IsSelected = !tag.IsSelected;
            return;
        }
        var pageSize = Math.Max(1, (int)(TagListScrollViewer.Viewport.Height / 48));
        var target = e.Key switch
        {
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.Home => 0,
            Key.End => picker.Tags.Count - 1,
            Key.PageUp => index - pageSize,
            Key.PageDown => index + pageSize,
            _ => -1
        };
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            FocusTagAt(Math.Clamp(target, 0, picker.Tags.Count - 1));
        }
    }

    private void FocusTagAt(int index)
    {
        if (_picker is not { IsActive: true } picker || index < 0 || index >= picker.Tags.Count)
            return;
        var tag = picker.Tags[index];
        var request = ++_focusRequest;
        _focusedTagIndex = index;
        TagListItems.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _focusRequest || !ReferenceEquals(_picker, picker) || !picker.IsActive || !IsEffectivelyVisible)
                return;
            var currentIndex = picker.Tags.IndexOf(tag);
            if (currentIndex < 0)
                return;
            var container = TagListItems.ContainerFromIndex(currentIndex);
            var checkBox = container?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            checkBox?.Focus(NavigationMethod.Directional);
            checkBox?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => TagSearchTextBox.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
}
