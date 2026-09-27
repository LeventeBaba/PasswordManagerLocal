namespace PasswordManagerLocal.Common.Backend.Services;

internal sealed class InteractiveSessionOperationLease : IDisposable
{
    private InteractiveSessionStateService? _owner;
    private readonly InteractiveSessionOperationLease? _previous;

    public InteractiveSessionOperationLease(
        InteractiveSessionStateService owner,
        InteractiveSessionOperationLease? previous)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _previous = previous;
    }

    public bool IsActive => Volatile.Read(ref _owner) is not null;

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;

        owner.CompleteOperation(this, _previous);
        GC.SuppressFinalize(this);
    }
}
