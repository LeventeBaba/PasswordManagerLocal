namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IRevocationDeliveryActivity
{
    bool HasActiveWork { get; }
    // Process-local evidence generation. Activity lease completion is only a wake-up;
    // it must not invalidate a successfully reconciled peer's discovery quiet period.
    long EvidenceVersion => 0;
    void NotifyCommittedChange() => RequestRefresh();
    IDisposable Enter();
    void RequestRefresh();
    IAsyncEnumerable<bool> ReadRefreshesAsync(CancellationToken ct);
}
