using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class RecordingWindowsIpcServerSessionFactory : IWindowsIpcServerSessionFactory
{
    private readonly Queue<BlockingWindowsIpcServerSession> _sessions = new();

    public IReadOnlyList<BlockingWindowsIpcServerSession> CreatedSessions => _created;
    private readonly List<BlockingWindowsIpcServerSession> _created = new();

    public void Enqueue(BlockingWindowsIpcServerSession session) => _sessions.Enqueue(session);

    public IWindowsIpcServerSession Create(IWindowsIpcConnection connection)
    {
        var session = _sessions.Count > 0
            ? _sessions.Dequeue()
            : new BlockingWindowsIpcServerSession();
        _created.Add(session);
        return session;
    }
}
