using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PasswordManagerLocal.Common.Frontend.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public ConfirmationDialog(string title, string message, string yesLabel, string noLabel)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yesLabel;
        NoButton.Content = noLabel;
    }

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close(true);
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
