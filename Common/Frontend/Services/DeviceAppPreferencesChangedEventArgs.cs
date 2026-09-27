namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class DeviceAppPreferencesChangedEventArgs : EventArgs
{
    public DeviceAppPreferencesChangedEventArgs(
        BackgroundSyncClientState state,
        bool wasOutcomeUncertain)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        WasOutcomeUncertain = wasOutcomeUncertain;
    }

    public BackgroundSyncClientState State { get; }
    public bool WasOutcomeUncertain { get; }
}
