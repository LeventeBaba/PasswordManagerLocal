using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages;

public partial class PasswordListView : UserControl
{
    private PasswordsViewModel? _observedViewModel;

    public PasswordListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as PasswordsViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        AttachViewModel(DataContext as PasswordsViewModel);
    }

    private void AttachViewModel(PasswordsViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.ListScrollToTopRequested -= HandleListScrollToTopRequested;
        }

        _observedViewModel = viewModel;

        if (_observedViewModel is not null)
        {
            _observedViewModel.ListScrollToTopRequested += HandleListScrollToTopRequested;
        }
    }

    private void HandleListScrollToTopRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => PasswordListScrollViewer.Offset = new Vector(PasswordListScrollViewer.Offset.X, 0),
            DispatcherPriority.Background);
    }
}
