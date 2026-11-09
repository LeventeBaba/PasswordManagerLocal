using Avalonia.Platform;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public sealed class AvaloniaFrontendLocalizationResourceLoader : IFrontendLocalizationResourceLoader
{
    private const string ResourceRoot =
        "avares://PasswordManagerLocal.Common.Frontend/Assets/Localization/";

    public ValueTask<Stream> OpenAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceName = language switch
        {
            AppLanguage.English => "en_us.json",
            AppLanguage.Hungarian => "hu.json",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
        };
        var resourceUri = new Uri($"{ResourceRoot}{resourceName}");
        if (!AssetLoader.Exists(resourceUri))
            throw new FileNotFoundException("The selected frontend localization resource is unavailable.");

        var stream = AssetLoader.Open(resourceUri);
        if (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return ValueTask.FromResult(stream);
    }
}
