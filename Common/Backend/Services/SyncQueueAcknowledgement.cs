using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

namespace PasswordManagerLocal.Common.Backend.Services;

internal static class SyncQueueAcknowledgement
{
    public static Task<bool> TryAcknowledgeAsync(
        this ISyncQueueRepository queue, Guid queueItemId, DateTimeOffset expectedEnqueuedAt,
        CancellationToken ct = default) =>
        queue.TryAcknowledgeAsync(queueItemId, expectedEnqueuedAt, DateTimeOffset.UtcNow, ct);
}
