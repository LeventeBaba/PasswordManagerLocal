using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class InMemoryApplicationPreferencesStore : IApplicationPreferencesStore
{
    private ApplicationPreferences _preferences;

    public InMemoryApplicationPreferencesStore(ApplicationPreferences preferences)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public int WriteCount { get; private set; }

    public Task<ApplicationPreferences> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_preferences);
    }

    public Task WriteAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        cancellationToken.ThrowIfCancellationRequested();
        _preferences = preferences;
        WriteCount++;
        return Task.CompletedTask;
    }
}
