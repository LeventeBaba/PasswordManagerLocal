using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class ManualFrontendDeadlineScheduler : IFrontendDeadlineScheduler
{
    private readonly List<Entry> _entries = [];

    public DateTimeOffset NowUtc { get; private set; } = DateTimeOffset.UnixEpoch;
    public int ActiveCount => _entries.Count(entry => !entry.IsDisposed && !entry.HasRun);

    public IDisposable Schedule(DateTimeOffset deadlineUtc, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new Entry(deadlineUtc.ToUniversalTime(), callback);
        _entries.Add(entry);
        return entry;
    }

    public void AdvanceTo(DateTimeOffset nowUtc)
    {
        NowUtc = nowUtc.ToUniversalTime();
        foreach (var entry in _entries
                     .Where(entry => !entry.IsDisposed && !entry.HasRun && entry.DeadlineUtc <= NowUtc)
                     .OrderBy(entry => entry.DeadlineUtc)
                     .ToArray())
        {
            entry.Run();
        }
    }

    private sealed class Entry(DateTimeOffset deadlineUtc, Action callback) : IDisposable
    {
        private readonly Action _callback = callback;

        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public bool IsDisposed { get; private set; }
        public bool HasRun { get; private set; }

        public void Run()
        {
            if (IsDisposed || HasRun)
                return;
            HasRun = true;
            _callback();
        }

        public void Dispose() => IsDisposed = true;
    }
}
