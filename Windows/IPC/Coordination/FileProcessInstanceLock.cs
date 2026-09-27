namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed class FileProcessInstanceLock : IProcessInstanceLock
{
    private readonly IProcessInstanceLockFileOpener _fileOpener;
    private readonly object _gate = new();
    private Stream? _ownershipHandle;
    private int _disposeStarted;

    public FileProcessInstanceLock(
        string lockFilePath,
        IProcessInstanceLockFileOpener? fileOpener = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        if (!Path.IsPathFullyQualified(lockFilePath))
            throw new ArgumentException("The process lock file path must be absolute.", nameof(lockFilePath));

        LockFilePath = Path.GetFullPath(lockFilePath);
        var directory = Path.GetDirectoryName(LockFilePath)
            ?? throw new ArgumentException("The process lock file path has no parent directory.", nameof(lockFilePath));
        Directory.CreateDirectory(directory);

        _fileOpener = fileOpener ?? new ProcessInstanceLockFileOpener();
        TryAcquire();
    }

    public string LockFilePath { get; }
    public bool IsOwner => Volatile.Read(ref _ownershipHandle) is not null;

    public bool TryAcquire()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_ownershipHandle is not null)
                return true;

            _ownershipHandle = _fileOpener.OpenExclusive(LockFilePath);
            return _ownershipHandle is not null;
        }
    }

    public void EnsureOwnership()
    {
        ThrowIfDisposed();
        if (!IsOwner)
            throw new ProcessInstanceAlreadyOwnedException(LockFilePath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        lock (_gate)
        {
            var ownershipHandle = _ownershipHandle;
            if (ownershipHandle is not null)
            {
                ownershipHandle.Dispose();
                _ownershipHandle = null;
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(FileProcessInstanceLock));
    }
}
