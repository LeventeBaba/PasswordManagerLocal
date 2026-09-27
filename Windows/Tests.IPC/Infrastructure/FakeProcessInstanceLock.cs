using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeProcessInstanceLock : IProcessInstanceLock
{
    private readonly FakeProcessInstanceLockState? _sharedState;
    private bool _ownsSharedState;

    public FakeProcessInstanceLock(bool isOwner = true)
    {
        IsOwner = isOwner;
        TryAcquireResult = isOwner;
    }

    public FakeProcessInstanceLock(FakeProcessInstanceLockState sharedState)
    {
        _sharedState = sharedState ?? throw new ArgumentNullException(nameof(sharedState));
        IsOwner = _ownsSharedState = sharedState.TryAcquire();
    }

    public string LockFilePath => @"C:\test\instance.lock";
    public bool IsOwner { get; private set; }
    public bool TryAcquireResult { get; set; }
    public bool IsDisposed { get; private set; }
    public int TryAcquireCount { get; private set; }
    public int EnsureOwnershipCount { get; private set; }
    public Exception? TryAcquireFailure { get; set; }
    public Exception? EnsureOwnershipFailure { get; set; }
    public Exception? DisposeFailure { get; set; }
    public ICollection<string>? OperationLog { get; set; }

    public bool TryAcquire()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(FakeProcessInstanceLock));
        TryAcquireCount++;
        if (TryAcquireFailure is not null)
            throw TryAcquireFailure;
        if (IsOwner)
            return true;

        if (_sharedState is not null)
            IsOwner = _ownsSharedState = _sharedState.TryAcquire();
        else
            IsOwner = TryAcquireResult;
        return IsOwner;
    }

    public void EnsureOwnership()
    {
        EnsureOwnershipCount++;
        OperationLog?.Add("lock-acquire");
        if (EnsureOwnershipFailure is not null)
            throw EnsureOwnershipFailure;
        if (!IsOwner)
            throw new ProcessInstanceAlreadyOwnedException(LockFilePath);
    }

    public void Dispose()
    {
        OperationLog?.Add("lock-release");
        if (DisposeFailure is not null)
            throw DisposeFailure;
        IsDisposed = true;
        ReleaseSharedState();
        IsOwner = false;
    }

    public void SimulateProcessTermination()
    {
        ReleaseSharedState();
        IsOwner = false;
    }

    private void ReleaseSharedState()
    {
        if (!_ownsSharedState)
            return;
        _ownsSharedState = false;
        _sharedState!.Release();
    }
}
