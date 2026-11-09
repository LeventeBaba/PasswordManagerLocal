using PasswordManagerLocal.Common.Frontend.Views.Styling;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Passwords;

public partial class PasswordEditorView : UserControl
{
    private bool _isRestoringColorSelection;
    private bool _isRestoringScroll;
    private int _restoreRequest;
    private PasswordsViewModel? _editor;

    public PasswordEditorView()
    {
        DeferredControlThemeResources.EnsureLoaded();
        InitializeComponent();
        SizeChanged += (_, e) => _editor?.SetEditorViewportHeight(e.NewSize.Height);
        AttachedToVisualTree += (_, _) => BindEditor();
        DetachedFromVisualTree += (_, _) => UnbindEditor();
        DataContextChanged += (_, _) => BindEditor();
        EditorScrollViewer.ScrollChanged += (_, _) =>
        {
            if (_editor is { IsEditorPaneVisible: true } && !_isRestoringScroll)
                _editor.EditorScrollOffset = EditorScrollViewer.Offset.Y;
        };
    }

    private void BindEditor()
    {
        UnbindEditor();
        if (TopLevel.GetTopLevel(this) is null || DataContext is not PasswordsViewModel editor)
            return;
        _editor = editor;
        editor.SetEditorViewportHeight(Bounds.Height);
        editor.PropertyChanged += HandleEditorChanged;
        var request = ++_restoreRequest;
        var offset = editor.EditorScrollOffset;
        _isRestoringScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _restoreRequest || !ReferenceEquals(_editor, editor))
                return;
            EditorScrollViewer.Offset = new Vector(0, offset);
            _isRestoringScroll = false;
            if (editor.RestoreEditorTagsFocus && !editor.IsPasswordTagPickerVisible)
            {
                editor.RestoreEditorTagsFocus = false;
                EditorTagSelector.FocusSelector();
            }
        }, DispatcherPriority.Loaded);
    }

    private void UnbindEditor()
    {
        _restoreRequest++;
        if (_editor is not null)
            _editor.PropertyChanged -= HandleEditorChanged;
        _editor = null;
        _isRestoringScroll = false;
    }

    private void HandleEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_editor is not { } editor)
            return;
        if (e.PropertyName is nameof(PasswordsViewModel.HasEditorNameValidationError)
            or nameof(PasswordsViewModel.HasEditorPasswordValidationError))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_editor, editor) || !editor.IsEditorPaneVisible || editor.IsPasswordTagPickerVisible)
                    return;
                TextBox? invalidField = editor.HasEditorNameValidationError ? EditorNameTextBox
                    : editor.HasEditorPasswordValidationError ? EditorPasswordTextBox : null;
                invalidField?.Focus(NavigationMethod.Directional);
                invalidField?.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

    internal void RestoreTagsFocus()
    {
        if (_editor is null)
            return;
        _editor.RestoreEditorTagsFocus = false;
        EditorTagSelector.FocusSelector();
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
