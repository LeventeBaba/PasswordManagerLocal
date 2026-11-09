using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages;

public partial class PasswordListView : UserControl
{
    private PasswordsViewModel? _page;
    private WeakReference<Control>? _discardReturnFocus;

    public PasswordListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindPage();
        AttachedToVisualTree += (_, _) => BindPage();
        DetachedFromVisualTree += (_, _) => UnbindPage();
        AddHandler(KeyDownEvent, HandlePageKeyDown, RoutingStrategies.Bubble);
    }

    private void BindPage()
    {
        UnbindPage();
        if (DataContext is not PasswordsViewModel page)
            return;
        _page = page;
        page.PropertyChanged += HandlePageChanged;
        if (page.IsPasswordTagPickerVisible)
            TagPickerSurface.PrepareForDisplay();
    }

    private void UnbindPage()
    {
        if (_page is not null)
            _page.PropertyChanged -= HandlePageChanged;
        _page = null;
    }

    private void HandlePageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_page is not { } page)
            return;
        if (e.PropertyName == nameof(PasswordsViewModel.IsPasswordTagPickerVisible))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_page, page) || TopLevel.GetTopLevel(this) is null)
                    return;
                if (page.IsPasswordTagPickerVisible)
                {
                    UpdatePickerSize();
                    TagPickerSurface.PrepareForDisplay();
                }
                else if (page.IsEditorPaneVisible && page.RestoreEditorTagsFocus)
                    this.GetVisualDescendants().OfType<PasswordEditorView>().LastOrDefault()?.RestoreTagsFocus();
            }, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(PasswordsViewModel.IsEditorDiscardConfirmationOpen))
        {
            if (page.IsEditorDiscardConfirmationOpen)
            {
                _discardReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused
                    ? new WeakReference<Control>(focused) : null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_page, page) && page.IsEditorDiscardConfirmationOpen)
                        KeepEditingButton.Focus(NavigationMethod.Directional);
                }, DispatcherPriority.Loaded);
            }
            else
            {
                var returnFocus = _discardReturnFocus;
                _discardReturnFocus = null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_page, page) && page.IsEditorPaneVisible && !page.IsPasswordTagPickerVisible
                        && returnFocus is not null && returnFocus.TryGetTarget(out var target) && target.IsEffectivelyVisible)
                        target.Focus(NavigationMethod.Directional);
                }, DispatcherPriority.Loaded);
            }
        }
    }

    private void OnTagPickerOverlaySizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePickerSize();

    private void UpdatePickerSize()
    {
        var compact = _page?.IsAndroidUi == true || TagPickerOverlay.Bounds.Width < 600 || TagPickerOverlay.Bounds.Height < 480;
        TagPickerSurface.MaxWidth = compact ? double.PositiveInfinity : 560;
        TagPickerSurface.MaxHeight = compact ? double.PositiveInfinity : 640;
        TagPickerSurface.Margin = new Thickness(compact ? 0 : 12);
        _page?.TagPicker.SetCompactLayout(TagPickerOverlay.Bounds.Height < 400);
    }

    private void HandlePageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _page is not { } page || page.IsPasswordTagPickerVisible || page.IsEditorDiscardConfirmationOpen)
            return;
        if (e.Source is Control source)
        {
            // The origin remains inside the picker even when Done/Cancel closes it
            // during this key event. Never let the same Enter also save the password.
            if (source.GetVisualAncestors().OfType<PasswordTagPickerView>().Any())
                return;
            if (e.Key == Key.Enter && (source is Button or ComboBox
                || source.GetVisualAncestors().Any(control => control is Button or ComboBox)))
                return;
            var textBox = source as TextBox ?? source.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
            if (e.Key == Key.Enter && textBox?.AcceptsReturn == true)
                return;
        }
        ICommand? command = null;
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control && page.IsEditorPaneVisible)
            command = page.SavePasswordCommand;
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            command = page.ExecutePrimaryActionCommand;
        if (command?.CanExecute(null) == true)
        {
            e.Handled = true;
            command.Execute(null);
        }
    }
}
