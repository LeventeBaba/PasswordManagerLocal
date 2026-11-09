using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

internal static class SyncQueueGeneration
{
    private static readonly object Gate = new();
    private static DateTimeOffset _last;

    public static DateTimeOffset Next(DateTimeOffset? previous = null, DateTimeOffset? proposed = null)
    {
        lock (Gate)
        {
            var next = UtcDateTimeUtil.ToUtc(proposed ?? DateTimeOffset.UtcNow);
            if (previous is { } current && next <= current)
                next = UtcDateTimeUtil.ToUtc(current).AddTicks(1);
            if (next <= _last)
                next = _last.AddTicks(1);

            _last = next;
            return next;
        }
    }
}
