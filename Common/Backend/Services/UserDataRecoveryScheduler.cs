using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

using PasswordManagerLocal.Common.Backend.Sync.Recovery;
namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDataRecoveryScheduler : IUserDataRecoveryScheduler
{
    private readonly Channel<UserDataRecoveryWorkItem> _channel = Channel.CreateUnbounded<UserDataRecoveryWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public void Schedule(Guid userId, UserDataRecoveryTrigger trigger)
    {
        if (userId == Guid.Empty || !_scheduled.TryAdd(userId, 0))
            return;

        if (!_channel.Writer.TryWrite(new UserDataRecoveryWorkItem(userId, trigger)))
            _scheduled.TryRemove(userId, out _);
    }

    internal IAsyncEnumerable<UserDataRecoveryWorkItem> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    // Remove the queued marker as soon as the single worker begins processing. New evidence that
    // arrives during a running attempt can then queue exactly one follow-up attempt without
    // creating concurrent work or losing the retrigger.
    internal void BeginProcessing(Guid userId) => _scheduled.TryRemove(userId, out _);
}
