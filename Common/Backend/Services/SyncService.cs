using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncService : ISyncService
{
    private readonly ISyncChangeQueueService _queue;

    public SyncService(ISyncChangeQueueService queue)
    {
        _queue = queue;
    }




    public async Task NeedsSyncAsync(
        Guid modelId,
        SyncModelType modelType,
        SyncChangeType changeType,
        CancellationToken ct = default)
    {
        var item = new SyncItem
        {
            ModelId = modelId,
            ModelType = modelType,
            ChangeType = changeType
        };

        await _queue.EnqueueAsync(item, ct);
    }
}
