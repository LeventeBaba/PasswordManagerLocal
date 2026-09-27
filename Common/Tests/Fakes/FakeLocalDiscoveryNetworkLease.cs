using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeLocalDiscoveryNetworkLease : ILocalDiscoveryNetworkLease
{
    private readonly IList<string>? _calls;

    public FakeLocalDiscoveryNetworkLease(IList<string>? calls = null)
    {
        _calls = calls;
    }

    public int AcquireCalls { get; private set; }
    public int ReleaseCalls { get; private set; }
    public bool IsAcquired { get; private set; }
    public Exception? AcquireFailure { get; set; }
    public Exception? ReleaseFailure { get; set; }

    public ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcquireCalls++;
        _calls?.Add("lease:acquire");
        if (AcquireFailure is not null)
            return ValueTask.FromException(AcquireFailure);

        IsAcquired = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync()
    {
        ReleaseCalls++;
        _calls?.Add("lease:release");
        IsAcquired = false;
        return ReleaseFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(ReleaseFailure);
    }
}
