using Avalonia.Controls;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordEditorView : UserControl
{
    private bool _isRestoringColorSelection;

    public PasswordEditorView()
    {
        InitializeComponent();
    }

    private void OnEditorColorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringColorSelection
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not PasswordColorOptionViewModel selectedOption
            || !selectedOption.IsManageColorsOption
            || DataContext is not PasswordsViewModel viewModel)
        {
            return;
        }

        // The manage-colors row is an action, not a selectable color. Capture the
        // last real color before explicitly invoking the navigation action. This is
        // intentionally done here as well as in the two-way setter because the
        // control-level selection event can run before the binding writes its value.
        var realColorOption = viewModel.SelectedEditorColorOption;
        viewModel.SelectedEditorColorOption = selectedOption;

        // The ComboBox changes its own visual selection before the view model can
        // reject the navigation row. Restore the real color immediately and once
        // more after the current selection event has completed.
        RestoreRealColorSelection(comboBox, realColorOption);

        Dispatcher.UIThread.Post(
            () => RestoreRealColorSelection(comboBox, realColorOption),
            DispatcherPriority.Background);
    }

    private void RestoreRealColorSelection(ComboBox comboBox, PasswordColorOptionViewModel? colorOption)
    {
        if (colorOption is null || colorOption.IsManageColorsOption)
        {
            return;
        }

        _isRestoringColorSelection = true;
        try
        {
            comboBox.SelectedItem = colorOption;
        }
        finally
        {
            _isRestoringColorSelection = false;
        }
    }
}
