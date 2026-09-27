namespace PasswordManagerLocal.Common.Contracts.Runtime;

public sealed class BackendRuntimeStateChangedEventArgs : EventArgs
{
    public BackendRuntimeStateChangedEventArgs(
        BackendRuntimeSnapshot previous,
        BackendRuntimeSnapshot current)
    {
        Previous = previous;
        Current = current;
    }

    public BackendRuntimeSnapshot Previous { get; }
    public BackendRuntimeSnapshot Current { get; }
}
