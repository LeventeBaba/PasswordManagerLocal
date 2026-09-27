using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceOnlineStatusEvaluator
{
    private readonly IBackendExecutionProfileProvider _executionProfileProvider;
    private readonly IDevicePresenceRegistry _presenceRegistry;

    public DeviceOnlineStatusEvaluator(
        IBackendExecutionProfileProvider executionProfileProvider,
        IDevicePresenceRegistry presenceRegistry)
    {
        _executionProfileProvider = executionProfileProvider
            ?? throw new ArgumentNullException(nameof(executionProfileProvider));
        _presenceRegistry = presenceRegistry
            ?? throw new ArgumentNullException(nameof(presenceRegistry));
    }

    public bool IsOnline(string tlsFingerprint)
    {
        var profile = _executionProfileProvider.Current;
        return profile is not null &&
            _presenceRegistry.IsOnline(tlsFingerprint, profile.DeviceOnlineTimeout);
    }
}
