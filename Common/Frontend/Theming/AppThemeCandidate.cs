namespace PasswordManagerLocal.Common.Frontend.Theming;

internal sealed class AppThemeCandidate : IDisposable
{
    private readonly AppThemeManager _owner;
    private int _committed;
    private int _disposed;

    public AppThemeCandidate(
        AppThemeManager owner,
        long generation,
        AppThemeDescriptor descriptor,
        IAppThemeResourceSet resources,
        CancellationTokenSource requestCancellation)
    {
        _owner = owner;
        Generation = generation;
        Descriptor = descriptor;
        Resources = resources;
        RequestCancellation = requestCancellation;
    }

    public long Generation { get; }
    public AppThemeDescriptor Descriptor { get; }
    public IAppThemeResourceSet Resources { get; }
    public CancellationTokenSource RequestCancellation { get; }
    public CancellationToken CancellationToken => RequestCancellation.Token;

    public void MarkCommitted() => Interlocked.Exchange(ref _committed, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (Volatile.Read(ref _committed) == 0)
        {
            _owner.Abandon(this);
            Resources.Dispose();
        }
        RequestCancellation.Dispose();
    }
}
