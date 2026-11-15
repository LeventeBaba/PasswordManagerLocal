namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IRevocationDeliveryActivity
{
    bool HasActiveWork { get; }
    IDisposable Enter();
    void RequestRefresh();
    IAsyncEnumerable<bool> ReadRefreshesAsync(CancellationToken ct);
}
