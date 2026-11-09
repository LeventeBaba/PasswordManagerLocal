namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>One UI-session motion policy. Refresh system state on Android resume.</summary>
public sealed class InterfaceMotionPolicy
{
    private readonly FrontendPlatformServices _platformServices;

    public InterfaceMotionPolicy(FrontendPlatformServices platformServices, bool userPreference)
    {
        _platformServices = platformServices;
        InterfaceAnimationsEnabled = userPreference;
        PlatformAnimationsEnabled = platformServices.AreAnimationsEnabled;
    }

    public event EventHandler? Changed;
    public bool IsAndroidUi => OperatingSystem.IsAndroid();
    public bool InterfaceAnimationsEnabled { get; private set; }
    public bool PlatformAnimationsEnabled { get; private set; }
    public bool EffectiveInterfaceAnimationsEnabled =>
        IsAndroidUi && InterfaceAnimationsEnabled && PlatformAnimationsEnabled;

    public void SetUserPreference(bool enabled)
    {
        if (InterfaceAnimationsEnabled == enabled)
            return;
        InterfaceAnimationsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshPlatformState()
    {
        var enabled = _platformServices.AreAnimationsEnabled;
        if (PlatformAnimationsEnabled == enabled)
            return;
        PlatformAnimationsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
