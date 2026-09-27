using Android.Content;
using Android.Net.Wifi;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

namespace PasswordManagerLocal.Android.Backend;

public sealed class AndroidLocalDiscoveryNetworkLease : ILocalDiscoveryNetworkLease
{
    private readonly Context _applicationContext;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WifiManager.MulticastLock? _multicastLock;
    private bool _acquired;

    public AndroidLocalDiscoveryNetworkLease(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _applicationContext = context.ApplicationContext
            ?? throw new InvalidOperationException("The Android application context is unavailable.");
    }

    public async ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_acquired)
                return;

            var wifiManager = _applicationContext.GetSystemService(Context.WifiService) as WifiManager
                ?? throw new InvalidOperationException("The Android Wi-Fi service is unavailable.");

            _multicastLock ??= wifiManager.CreateMulticastLock("PasswordManagerLocal.LocalDiscovery")
                ?? throw new InvalidOperationException("The Android multicast lock could not be created.");
            _multicastLock.SetReferenceCounted(false);

            if (!_multicastLock.IsHeld)
                _multicastLock.Acquire();

            _acquired = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask ReleaseAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (!_acquired)
                return;

            if (_multicastLock?.IsHeld == true)
                _multicastLock.Release();

            _acquired = false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }
}
