using Avalonia;
using Avalonia.Controls;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public sealed class ExternalLinkButton : Button
{
    public static readonly StyledProperty<Uri?> ExternalUriProperty =
        AvaloniaProperty.Register<ExternalLinkButton, Uri?>(nameof(ExternalUri));

    public static readonly StyledProperty<bool> AllowMailtoProperty =
        AvaloniaProperty.Register<ExternalLinkButton, bool>(nameof(AllowMailto));

    public bool AllowMailto
    {
        get => GetValue(AllowMailtoProperty);
        set => SetValue(AllowMailtoProperty, value);
    }

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
            (uri.Scheme != Uri.UriSchemeHttps && !(AllowMailto && uri.Scheme == Uri.UriSchemeMailto)))
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
            // The page remains readable when no external handler is available.
        }
    }
}
