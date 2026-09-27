namespace PasswordManagerLocal.Windows.Agent.Hosting;

internal sealed class WindowsAgentAdmissionLease : IDisposable
{
    private readonly Action _release;
    private int _disposed;

    public WindowsAgentAdmissionLease(Action release) =>
        _release = release ?? throw new ArgumentNullException(nameof(release));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _release();
    }
}