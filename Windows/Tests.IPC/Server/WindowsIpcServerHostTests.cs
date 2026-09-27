using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Server;

[TestClass]
public sealed class WindowsIpcServerHostTests
{
    [TestMethod]
    public async Task TracksAndRemovesCompletedSessions()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        var session = new BlockingWindowsIpcServerSession();
        factory.Enqueue(session);
        await using var host = new WindowsIpcServerHost(listener, factory);
        await host.StartAsync();

        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);
        session.Complete();
        await WaitUntilAsync(() => host.ActiveSessionCount == 0);

        Assert.AreEqual(1, session.RunCount);
        Assert.AreEqual(1, session.DisposeCount);
    }

    [TestMethod]
    public async Task SessionFailureDoesNotStopAcceptLoop()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        factory.Enqueue(new BlockingWindowsIpcServerSession { FailRun = true });
        var second = new BlockingWindowsIpcServerSession();
        factory.Enqueue(second);
        await using var host = new WindowsIpcServerHost(listener, factory);
        await host.StartAsync();

        listener.Queue(new DisposableTestWindowsIpcConnection());
        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => factory.CreatedSessions.Count == 2);

        Assert.IsFalse(host.Completion.IsCompleted);
        second.Complete();
    }

    [TestMethod]
    public async Task ConnectionLimitClosesExcessConnection()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        factory.Enqueue(new BlockingWindowsIpcServerSession());
        var excess = new DisposableTestWindowsIpcConnection();
        await using var host = new WindowsIpcServerHost(
            listener,
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 1));
        await host.StartAsync();

        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);
        listener.Queue(excess);
        await WaitUntilAsync(() => excess.DisposeCount == 1);

        Assert.AreEqual(1, factory.CreatedSessions.Count);
    }

    [TestMethod]
    public async Task ClosingActiveSessionsKeepsListenerAvailableForReplacementConnection()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        var first = new BlockingWindowsIpcServerSession();
        var second = new BlockingWindowsIpcServerSession();
        factory.Enqueue(first);
        factory.Enqueue(second);
        await using var host = new WindowsIpcServerHost(listener, factory);
        await host.StartAsync();
        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);

        await host.CloseActiveSessionsAsync();
        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => factory.CreatedSessions.Count == 2);

        Assert.IsNull(host.ListenerFailure);
        Assert.IsFalse(host.Completion.IsCompleted);
        Assert.IsTrue(first.DisposeCount >= 1);
        second.Complete();
    }

    [TestMethod]
    public async Task ShutdownStopsAcceptanceAndDisposesSessions()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        var session = new BlockingWindowsIpcServerSession();
        factory.Enqueue(session);
        await using var host = new WindowsIpcServerHost(listener, factory);
        await host.StartAsync();
        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);

        await host.StopAsync();

        Assert.AreEqual(0, host.ActiveSessionCount);
        Assert.IsTrue(session.DisposeCount >= 1);
    }

    [TestMethod]
    public async Task StopWaitsForSessionDisposalCompletion()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        var factory = new RecordingWindowsIpcServerSessionFactory();
        var session = new BlockingWindowsIpcServerSession { BlockDispose = true };
        factory.Enqueue(session);
        await using var host = new WindowsIpcServerHost(listener, factory);
        await host.StartAsync();
        listener.Queue(new DisposableTestWindowsIpcConnection());
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);

        var stopTask = host.StopAsync();
        await WaitUntilAsync(() => session.DisposeCount == 1);

        Assert.IsFalse(stopTask.IsCompleted);
        Assert.AreEqual(1, host.ActiveSessionCount);

        session.CompleteDispose();
        await stopTask;

        Assert.AreEqual(0, host.ActiveSessionCount);
        Assert.AreEqual(1, session.DisposeCount);
    }

    [TestMethod]
    public async Task ListenerFailureIsSurfaced()
    {
        var listener = new ChannelWindowsIpcConnectionListener();
        await using var host = new WindowsIpcServerHost(
            listener,
            new RecordingWindowsIpcServerSessionFactory());
        await host.StartAsync();

        listener.Fail(new IOException("listener failed"));
        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await host.Completion);

        Assert.IsInstanceOfType<IOException>(host.ListenerFailure);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
