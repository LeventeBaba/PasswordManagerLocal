namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IApplicationPreferencesChangeNotifier
{
    Task NotifyLanguagePersistedAsync(CancellationToken cancellationToken = default);
}
