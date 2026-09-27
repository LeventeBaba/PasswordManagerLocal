using Avalonia.Controls;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordTagEditorView : UserControl
{
    private bool _isRestoringColorSelection;

    public PasswordTagEditorView()
    {
        InitializeComponent();
    }

    private void OnPasswordTagColorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringColorSelection
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not PasswordColorOptionViewModel selectedOption
            || !selectedOption.IsManageColorsOption
            || DataContext is not PasswordsViewModel viewModel)
        {
            return;
        }

        var realColorOption = viewModel.SelectedPasswordTagColorOption;
        viewModel.SelectedPasswordTagColorOption = selectedOption;
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
