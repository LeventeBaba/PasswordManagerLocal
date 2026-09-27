using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class MutableApplicationPreferencesStore : IApplicationPreferencesStore
{
    private readonly object _gate = new();
    private ApplicationPreferences _current;

    public MutableApplicationPreferencesStore(AppLanguage language, AppThemeMode theme = AppThemeMode.Light) =>
        _current = new ApplicationPreferences { Language = language, Theme = theme };

    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public Func<CancellationToken, Task<ApplicationPreferences>>? ReadOverride { get; set; }
    public ICollection<string>? OperationLog { get; set; }

    public ApplicationPreferences Current
    {
        get { lock (_gate) return _current; }
        set { lock (_gate) _current = value; }
    }

    public async Task<ApplicationPreferences> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        OperationLog?.Add("read");
        if (ReadFailure is not null)
            throw ReadFailure;
        if (ReadOverride is not null)
            return await ReadOverride(cancellationToken);
        lock (_gate)
            return _current;
    }

    public Task WriteAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        OperationLog?.Add("write");
        if (WriteFailure is not null)
            return Task.FromException(WriteFailure);
        Current = preferences;
        return Task.CompletedTask;
    }
}
