using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public partial class PasswordTagSelectorView : UserControl
{
    public PasswordTagSelectorView()
    {
        InitializeComponent();
    }

    private void OnTagSearchGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is PasswordsViewModel viewModel)
        {
            viewModel.IsEditorTagSearchFocused = true;
        }
    }

    private void OnSelectorLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsKeyboardFocusWithin && DataContext is PasswordsViewModel viewModel)
            {
                viewModel.IsEditorTagSearchFocused = false;
            }
        }, DispatcherPriority.Background);
    }

    private void OnTagSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PasswordsViewModel viewModel)
        {
            return;
        }

        viewModel.SelectFirstEditorTagSuggestion();
        e.Handled = true;
    }
}
