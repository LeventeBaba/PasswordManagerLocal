using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public interface IFrontendLocalizationResourceLoader
{
    ValueTask<Stream> OpenAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default);
}
