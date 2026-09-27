namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class SyncRuntimeStateChangedEventArgs : EventArgs
{
    public SyncRuntimeStateChangedEventArgs(
        SyncRuntimeSnapshot previous,
        SyncRuntimeSnapshot current)
    {
        Previous = previous;
        Current = current;
    }

    public SyncRuntimeSnapshot Previous { get; }
    public SyncRuntimeSnapshot Current { get; }
}
