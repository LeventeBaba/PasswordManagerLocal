using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Services;
using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserPostLoginWorkSchedulerTests
{
    [TestMethod]
    public async Task IdleConsumer_WakesOnNewWorkAndCanBeCanceledWithoutPolling()
    {
        var scheduler = new UserPostLoginWorkScheduler(TimeProvider.System);
        var waiting = scheduler.WaitForWorkAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        MSTestAssert.IsFalse(waiting.IsCompleted);
        scheduler.Schedule(Guid.NewGuid());
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Clear();

        using var lifetime = new CancellationTokenSource();
        var canceled = scheduler.WaitForWorkAsync(TimeSpan.FromMinutes(1), lifetime.Token);
        lifetime.Cancel();
        await MSTestAssert.ThrowsAsync<OperationCanceledException>(() => canceled);
    }

    [TestMethod]
    public void InitialReadGate_CoalescesSyncAndRequiresTheIssuingToken()
    {
        var scheduler = new UserPostLoginWorkScheduler(new ManualTime());
        var user = Guid.NewGuid();
        var token = Guid.NewGuid();
        scheduler.DeferUntilInitialRead(user, 2, 3, token);
        scheduler.Schedule(user);
        scheduler.ReleaseInitialRead(user, Guid.NewGuid());
        MSTestAssert.IsTrue(scheduler.IsInitialReadPending(user));
        MSTestAssert.IsNull(scheduler.TryTakeReady());

        scheduler.ReleaseInitialRead(user, token);
        var work = scheduler.TryTakeReady();
        MSTestAssert.IsNotNull(work);
        MSTestAssert.AreEqual(token, work.Token);
        MSTestAssert.AreEqual(2L, work.KeyEpoch);
        MSTestAssert.IsNull(scheduler.TryTakeReady());
        scheduler.Complete(work, retry: false);
        MSTestAssert.IsNull(scheduler.TryTakeReady());
    }

    [TestMethod]
    public void MissingInitialRead_HasABoundedFallbackWithoutBlockingOtherUsers()
    {
        var time = new ManualTime();
        var scheduler = new UserPostLoginWorkScheduler(time);
        var waiting = Guid.NewGuid();
        var ready = Guid.NewGuid();
        scheduler.DeferUntilInitialRead(waiting, 1, 1, Guid.NewGuid());
        scheduler.Schedule(ready);
        var first = scheduler.TryTakeReady()!;
        MSTestAssert.AreEqual(ready, first.UserId);
        scheduler.Complete(first, retry: false);
        time.Advance(UserPostLoginWorkScheduler.InitialReadTimeout);
        MSTestAssert.IsFalse(scheduler.IsInitialReadPending(waiting));
        var second = scheduler.TryTakeReady()!;
        MSTestAssert.AreEqual(waiting, second.UserId);
        scheduler.Complete(second, retry: false);
    }

    [TestMethod]
    public void Logout_CancelsRunningWorkAndOldCompletionCannotRetireANewSession()
    {
        var scheduler = new UserPostLoginWorkScheduler(new ManualTime());
        var user = Guid.NewGuid();
        var oldToken = Guid.NewGuid();
        var newToken = Guid.NewGuid();
        scheduler.DeferUntilInitialRead(user, 1, 1, oldToken);
        scheduler.ReleaseInitialRead(user, oldToken);
        var old = scheduler.TryTakeReady()!;
        scheduler.CancelToken(oldToken);
        MSTestAssert.IsTrue(old.Cancellation.IsCancellationRequested);
        scheduler.DeferUntilInitialRead(user, 2, 1, newToken);
        scheduler.Complete(old, retry: true);
        MSTestAssert.IsTrue(scheduler.IsInitialReadPending(user));
        scheduler.ReleaseInitialRead(user, newToken);
        var current = scheduler.TryTakeReady()!;
        MSTestAssert.AreEqual(2L, current.KeyEpoch);
        scheduler.Complete(current, retry: false);
    }

    [TestMethod]
    public void TriggersDuringWork_RequestOneFollowUpAndFailuresBackOff()
    {
        var time = new ManualTime();
        var scheduler = new UserPostLoginWorkScheduler(time);
        var user = Guid.NewGuid();
        scheduler.Schedule(user);
        var first = scheduler.TryTakeReady()!;
        scheduler.Schedule(user);
        scheduler.Schedule(user);
        scheduler.Complete(first, retry: false);
        var retry = scheduler.TryTakeReady()!;
        scheduler.Complete(retry, retry: true);
        MSTestAssert.IsNull(scheduler.TryTakeReady());
        time.Advance(TimeSpan.FromSeconds(30));
        var last = scheduler.TryTakeReady()!;
        scheduler.Complete(last, retry: false);
        MSTestAssert.IsNull(scheduler.TryTakeReady());
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
