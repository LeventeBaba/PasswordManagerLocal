using System.Threading.Channels;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

/// <summary>One-shot delay gates. Only the test can fire a registered timer; no wall-clock retries.</summary>
internal sealed class ControlledDelayTimeProvider : TimeProvider
{
    private readonly Channel<Delay> _registrations = Channel.CreateBounded<Delay>(64);
    private int _active;
    public int ActiveTimers => Volatile.Read(ref _active);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot delays are supported.");
        var timer = new Delay(this, callback, state, dueTime);
        Interlocked.Increment(ref _active);
        if (dueTime != Timeout.InfiniteTimeSpan && !_registrations.Writer.TryWrite(timer))
        {
            timer.Dispose();
            throw new InvalidOperationException("The test has not consumed its timer registrations.");
        }
        return timer;
    }

    public async Task<Delay> NextAsync(TimeSpan? expected = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var timer = await _registrations.Reader.ReadAsync(timeout.Token);
            if (timer.IsDisposed) continue;
            if (expected.HasValue && timer.DueTime != expected.Value)
                throw new InvalidOperationException($"Expected delay {expected}, registered {timer.DueTime}.");
            return timer;
        }
    }

    internal sealed class Delay(ControlledDelayTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _disposed;
        public TimeSpan DueTime => dueTime;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Fire()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref owner._active);
            callback(state);
        }
        public bool Change(TimeSpan due, TimeSpan period) => throw new NotSupportedException();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._active);
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
