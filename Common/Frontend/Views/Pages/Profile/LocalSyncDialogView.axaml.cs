using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Views.Pages.Profile;

public partial class LocalSyncDialogView : UserControl
{
    public LocalSyncDialogView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible)
                    this.FindControl<Button>("LocalSyncCancelButton")?.Focus();
            });
    }
}
