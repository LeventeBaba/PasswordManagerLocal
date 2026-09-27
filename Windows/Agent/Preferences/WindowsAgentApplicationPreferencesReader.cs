using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Windows.Agent.Preferences;

public sealed class WindowsAgentApplicationPreferencesReader
{
    private readonly IApplicationPreferencesStore _store;

    public WindowsAgentApplicationPreferencesReader(IApplicationPreferencesStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AppLanguage> ReadLanguageAsync(
        CancellationToken cancellationToken = default)
    {
        var preferences = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return preferences.Language;
    }
}
