using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal sealed class PeerPenaltyRecorder
{
    private readonly ConcurrentQueue<(Guid PeerId, string Reason)> _attempts = new();
    public void Record(Guid peerId, string reason) => _attempts.Enqueue((peerId, reason));
    public IReadOnlyList<(Guid PeerId, string Reason)> Snapshot() => _attempts.ToArray();
}
