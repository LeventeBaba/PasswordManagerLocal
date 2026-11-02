using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Profile;

public partial class DeviceHistoryRecoveryDialogView : UserControl
{
    public DeviceHistoryRecoveryDialogView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("HistoryRecoveryCancelButton")?.Focus());
    }
}
