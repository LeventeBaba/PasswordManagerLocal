namespace PasswordManagerLocal.Windows.Agent.Tray;

public sealed class TrayIconMouseEventArgs : EventArgs
{
    public TrayIconMouseEventArgs(TrayIconMouseButton button, int clicks)
    {
        Button = button;
        Clicks = clicks;
    }

    public TrayIconMouseButton Button { get; }
    public int Clicks { get; }
}
