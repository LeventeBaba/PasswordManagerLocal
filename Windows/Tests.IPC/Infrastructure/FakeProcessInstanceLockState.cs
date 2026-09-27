namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeProcessInstanceLockState
{
    private readonly object _gate = new();
    private bool _held;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_held)
                return false;
            _held = true;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
            _held = false;
    }
}
