using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Preferences;

public interface IApplicationPreferencesStore
{
    Task<ApplicationPreferences> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default);
}
