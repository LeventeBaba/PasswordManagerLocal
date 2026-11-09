namespace PasswordManagerLocal.Common.Frontend.Localization;

internal sealed class FrontendLocalizationCandidate : IDisposable
{
    private readonly FrontendLocalizationService _owner;
    private int _committed;
    private int _disposed;

    public FrontendLocalizationCandidate(
        FrontendLocalizationService owner,
        long generation,
        FrontendTranslationSnapshot snapshot,
        CancellationTokenSource requestCancellation)
    {
        _owner = owner;
        Generation = generation;
        Snapshot = snapshot;
        RequestCancellation = requestCancellation;
    }

    public long Generation { get; }
    public FrontendTranslationSnapshot Snapshot { get; }
    public CancellationTokenSource RequestCancellation { get; }
    public CancellationToken CancellationToken => RequestCancellation.Token;

    public void MarkCommitted() => Interlocked.Exchange(ref _committed, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (Volatile.Read(ref _committed) == 0)
            _owner.Abandon(this);
        RequestCancellation.Dispose();
    }
}
