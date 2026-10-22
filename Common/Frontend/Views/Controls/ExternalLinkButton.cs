using Avalonia;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public sealed class ExternalLinkButton : Button
{
    public static readonly StyledProperty<Uri?> ExternalUriProperty =
        AvaloniaProperty.Register<ExternalLinkButton, Uri?>(nameof(ExternalUri));

    public Uri? ExternalUri
    {
        get => GetValue(ExternalUriProperty);
        set => SetValue(ExternalUriProperty, value);
    }

    protected override async void OnClick()
    {
        base.OnClick();
        var uri = ExternalUri;
        if (uri is null ||
            !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
            return;

        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // The offline notice remains readable even when no browser handler exists.
        }
    }
}
