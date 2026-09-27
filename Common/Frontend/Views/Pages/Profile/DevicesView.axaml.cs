using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Profile;

public partial class DevicesView : UserControl
{
    private ProfileViewModel? _observedViewModel;

    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as ProfileViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        AttachViewModel(DataContext as ProfileViewModel);
    }

    private void AttachViewModel(ProfileViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.DeviceListScrollToTopRequested -= HandleDeviceListScrollToTopRequested;
        }

        _observedViewModel = viewModel;

        if (_observedViewModel is not null)
        {
            _observedViewModel.DeviceListScrollToTopRequested += HandleDeviceListScrollToTopRequested;
        }
    }

    private void HandleDeviceListScrollToTopRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => DeviceListScrollViewer.Offset = new Vector(DeviceListScrollViewer.Offset.X, 0),
            DispatcherPriority.Background);
    }
}
