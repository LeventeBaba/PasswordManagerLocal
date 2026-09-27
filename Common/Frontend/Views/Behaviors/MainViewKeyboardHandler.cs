using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend.Views.Behaviors;

internal sealed class MainViewKeyboardHandler
{
    private readonly MainView _view;

    public MainViewKeyboardHandler(MainView view)
    {
        _view = view;
    }

    public async Task HandleTopLevelKeyDownAsync(KeyEventArgs e)
    {
        if (await TryHandleRefreshShortcutAsync(e))
            return;

        if (await TryHandleConfirmationShortcutAsync(e))
            return;

        if (e.Handled
            || OperatingSystem.IsAndroid()
            || e.Key != Key.Escape
            || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        await _view.HandleBackRequestAsync();
    }

    public async Task HandleKeyDownAsync(KeyEventArgs e)
    {
        if (e.Handled)
            return;

        if (await TryHandleRefreshShortcutAsync(e))
            return;

        if (!OperatingSystem.IsAndroid()
            && e.Key == Key.Escape
            && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            await _view.HandleBackRequestAsync();
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != KeyModifiers.Control || e.Source is not Control sourceControl)
            return;

        var textBox = TextBoxClipboardHandler.FindSourceTextBox(sourceControl);
        if (textBox is null)
            return;

        if (e.Key == Key.C)
            await TextBoxClipboardHandler.CopySelectedTextAsync(textBox, e, _view.PlatformServices?.Clipboard);
        else if (e.Key == Key.X)
            await TextBoxClipboardHandler.CutSelectedTextAsync(textBox, e, _view.PlatformServices?.Clipboard);
    }

    public async Task HandleCopyingToClipboardAsync(RoutedEventArgs e)
    {
        if (!e.Handled && e.Source is TextBox textBox)
            await TextBoxClipboardHandler.CopySelectedTextAsync(textBox, e, _view.PlatformServices?.Clipboard);
    }

    public async Task HandleCuttingToClipboardAsync(RoutedEventArgs e)
    {
        if (!e.Handled && e.Source is TextBox textBox)
            await TextBoxClipboardHandler.CutSelectedTextAsync(textBox, e, _view.PlatformServices?.Clipboard);
    }

    private async Task<bool> TryHandleRefreshShortcutAsync(KeyEventArgs e)
    {
        if (!OperatingSystem.IsWindows()
            || e.Handled
            || e.Key != Key.F5
            || e.KeyModifiers != KeyModifiers.None
            || _view.DataContext is not MainViewModel { IsAuthenticated: true, IsApplicationInteractionEnabled: true } viewModel)
        {
            return false;
        }

        e.Handled = true;
        await viewModel.RequestRefreshVisiblePageAsync();
        return true;
    }

    private async Task<bool> TryHandleConfirmationShortcutAsync(KeyEventArgs e)
    {
        if (!OperatingSystem.IsWindows()
            || e.Handled
            || e.Key != Key.Enter
            || e.KeyModifiers != KeyModifiers.None
            || _view.DataContext is not MainViewModel viewModel
            || !viewModel.HasConfirmableDialogOpen)
        {
            return false;
        }

        // Mark the routed event handled before awaiting so the focused control cannot
        // also process Enter while the confirmation operation is running.
        e.Handled = true;
        await viewModel.ConfirmOpenDialogAsync();
        return true;
    }

    internal async Task<bool> HandleBackRequestCoreAsync()
    {
        if (_view.DataContext is not MainViewModel viewModel)
            return true;

        if (await viewModel.TryNavigateBackAsync())
            return true;

        if (!OperatingSystem.IsWindows())
            return false;

        if (viewModel.IsAuthenticated)
        {
            if (await ShowConfirmationDialogAsync(
                    viewModel.LogoutConfirmationTitle,
                    viewModel.LogoutConfirmationMessage,
                    viewModel.YesLabel,
                    viewModel.NoLabel))
            {
                await viewModel.RequestLogoutAsync();
            }

            return true;
        }

        if (await ShowConfirmationDialogAsync(
                viewModel.ExitConfirmationTitle,
                viewModel.ExitConfirmationMessage,
                viewModel.YesLabel,
                viewModel.NoLabel))
        {
            (TopLevel.GetTopLevel(_view) as Window)?.Close();
        }

        return true;
    }

    private async Task<bool> ShowConfirmationDialogAsync(string title, string message, string yesLabel, string noLabel)
    {
        if (TopLevel.GetTopLevel(_view) is not Window owner)
            return false;

        var dialog = new ConfirmationDialog(title, message, yesLabel, noLabel);
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
