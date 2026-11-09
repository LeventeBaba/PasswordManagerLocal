namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>Optional runtime-client surface for push invalidations.</summary>
public interface IFrontendChangeStreamClient
{
    IAsyncEnumerable<FrontendChangeStreamEvent> ReadChangeStreamAsync(
        Guid? userId = null,
        CancellationToken cancellationToken = default);
}
