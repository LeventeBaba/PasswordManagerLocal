using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Coalesced, metadata-only work. Durable pending snapshots remain the recovery source of truth.
/// No request task, encryption key, decrypted bundle, or scoped service is retained here.
/// </summary>
public sealed class UserPostLoginWorkScheduler(TimeProvider time) : IUserPostLoginWorkScheduler
{
    internal static readonly TimeSpan InitialReadTimeout = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Work> _pending = [];
    private TaskCompletionSource _changed = NewSignal();

    public void DeferUntilInitialRead(Guid userId, long keyEpoch, long membershipEpoch, Guid token)
    {
        Work? previous;
        lock (_gate)
        {
            _pending.Remove(userId, out previous);
            _pending.Add(userId, new Work(userId, token, keyEpoch, membershipEpoch,
                time.GetUtcNow() + InitialReadTimeout) { WaitingForInitialRead = true });
            NotifyLocked();
        }
        Cancel(previous);
    }

    public void ReleaseInitialRead(Guid userId, Guid token)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(userId, out var work) && work.Token == token)
            {
                work.WaitingForInitialRead = false;
                work.ReadyAt = time.GetUtcNow();
                NotifyLocked();
            }
        }
    }

    public bool IsInitialReadPending(Guid userId)
    {
        lock (_gate)
            return _pending.TryGetValue(userId, out var work) && work.WaitingForInitialRead &&
                   work.ReadyAt > time.GetUtcNow();
    }

    public void Schedule(Guid userId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(userId, out var work))
            {
                if (work.Running) work.Dirty = true;
                NotifyLocked();
                return; // Receiving sync must not release the initial-read gate.
            }
            _pending.Add(userId, new Work(userId, null, null, null, time.GetUtcNow()));
            NotifyLocked();
        }
    }

    public void CancelToken(Guid token)
    {
        Work[] canceled;
        lock (_gate)
        {
            canceled = _pending.Values.Where(work => work.Token == token).ToArray();
            foreach (var work in canceled) _pending.Remove(work.UserId);
            NotifyLocked();
        }
        foreach (var work in canceled) Cancel(work);
    }

    internal Work? TryTakeReady()
    {
        lock (_gate)
        {
            var work = _pending.Values.Where(work => !work.Running && work.ReadyAt <= time.GetUtcNow())
                .OrderBy(work => work.ReadyAt).FirstOrDefault();
            if (work is not null)
            {
                work.Running = true;
                work.WaitingForInitialRead = false;
                work.Dirty = false;
            }
            return work;
        }
    }

    internal void Complete(Work work, bool retry)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(work.UserId, out var current) && ReferenceEquals(current, work))
            {
                if (!work.Cancellation.IsCancellationRequested && (retry || work.Dirty))
                {
                    work.Running = false;
                    work.ReadyAt = time.GetUtcNow() + (retry ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
                    NotifyLocked();
                    return;
                }
                _pending.Remove(work.UserId);
            }
        }
        work.DisposeCancellation();
    }

    internal void Clear()
    {
        Work[] canceled;
        lock (_gate) { canceled = _pending.Values.ToArray(); _pending.Clear(); NotifyLocked(); }
        foreach (var work in canceled) Cancel(work);
    }

    private static void Cancel(Work? work)
    {
        if (work is null) return;
        // Running tickets own disposal until the host consumer has finished with them.
        work.Cancel();
    }

    internal async Task WaitForWorkAsync(TimeSpan maximumDelay, CancellationToken ct)
    {
        Task changed;
        TimeSpan delay;
        lock (_gate)
        {
            changed = _changed.Task;
            delay = maximumDelay;
            foreach (var work in _pending.Values.Where(work => !work.Running))
            {
                var remaining = work.ReadyAt - time.GetUtcNow();
                if (remaining < delay) delay = remaining;
            }
        }
        if (delay <= TimeSpan.Zero) return;
        try { await changed.WaitAsync(delay, time, ct); }
        catch (TimeoutException) { }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void NotifyLocked()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    internal sealed class Work(Guid userId, Guid? token, long? keyEpoch, long? membershipEpoch, DateTimeOffset readyAt)
    {
        private readonly object _cancellationGate = new();
        private bool _disposed;
        public Guid UserId { get; } = userId;
        public Guid? Token { get; } = token;
        public long? KeyEpoch { get; } = keyEpoch;
        public long? MembershipEpoch { get; } = membershipEpoch;
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset ReadyAt { get; set; } = readyAt;
        public bool WaitingForInitialRead { get; set; }
        public bool Running { get; set; }
        public bool Dirty { get; set; }

        public void Cancel()
        {
            lock (_cancellationGate)
            {
                if (_disposed) return;
                Cancellation.Cancel();
                if (!Running) DisposeCancellation();
            }
        }

        public void DisposeCancellation()
        {
            lock (_cancellationGate)
            {
                if (_disposed) return;
                _disposed = true;
                Cancellation.Dispose();
            }
        }
    }
}
