using System.Text;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class TestFrontendLocalizationResourceLoader :
    IFrontendLocalizationResourceLoader
{
    public ValueTask<Stream> OpenAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = language switch
        {
            AppLanguage.English => "English",
            AppLanguage.Hungarian => "Magyar",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
        };
        return ValueTask.FromResult<Stream>(new MemoryStream(
            Encoding.UTF8.GetBytes($"{{\"Test\":\"{value}\"}}"),
            writable: false));
    }
}
