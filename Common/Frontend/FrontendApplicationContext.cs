using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Frontend;

public sealed record FrontendApplicationContext
{
    public FrontendApplicationContext(
        IFrontendBackendClient<IEndpoints> backendClient,
        IBackgroundSyncSettingsClient backgroundSyncSettingsClient,
        string applicationDataDirectory,
        Action? desktopExitRequested = null,
        IApplicationPreferencesStore? applicationPreferencesStore = null,
        IApplicationPreferencesChangeNotifier? applicationPreferencesChangeNotifier = null,
        FrontendPlatformServices? platformServices = null)
    {
        BackendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
        BackgroundSyncSettingsClient = backgroundSyncSettingsClient
            ?? throw new ArgumentNullException(nameof(backgroundSyncSettingsClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ApplicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        DesktopExitRequested = desktopExitRequested;
        ApplicationPreferencesStore = applicationPreferencesStore ??
            new FileApplicationPreferencesStore(ApplicationDataDirectory);
        ApplicationPreferencesChangeNotifier = applicationPreferencesChangeNotifier;
        PlatformServices = platformServices ?? new FrontendPlatformServices();
    }

    public IFrontendBackendClient<IEndpoints> BackendClient { get; }
    public IBackgroundSyncSettingsClient BackgroundSyncSettingsClient { get; }
    public string ApplicationDataDirectory { get; }
    public Action? DesktopExitRequested { get; }
    public IApplicationPreferencesStore ApplicationPreferencesStore { get; }
    public IApplicationPreferencesChangeNotifier? ApplicationPreferencesChangeNotifier { get; }
    public FrontendPlatformServices PlatformServices { get; }
}
