namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal sealed class EndpointRpcAdmissionLease : IDisposable
{
    private readonly SemaphoreSlim _capacity;
    private int _released;

    public EndpointRpcAdmissionLease(SemaphoreSlim capacity) =>
        _capacity = capacity ?? throw new ArgumentNullException(nameof(capacity));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _capacity.Release();
    }
}
