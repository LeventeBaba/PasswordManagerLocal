using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IRevocationReceiptService
{
    // Caller owns the application transaction; this method neither commits nor publishes a network acknowledgment.
    Task StageAppliedAsync(UserControlOperationEnvelope operation, CancellationToken ct = default);
    Task<IReadOnlyList<RevocationReceiptInventoryEntry>> GetInventoryAsync(Guid operationId, CancellationToken ct = default);
    Task<IReadOnlyList<byte[]>> BuildRequestedAsync(IEnumerable<RevocationReceiptInventoryEntry> requests,
        IReadOnlySet<Guid> allowedOperations, CancellationToken ct = default);
    Task ObservePeerInventoryAsync(Guid peerDeviceId, IEnumerable<UserControlOperationUserInventory> inventory, CancellationToken ct = default);
    Task StoreAsync(IEnumerable<byte[]> payloads, IReadOnlySet<Guid> allowedOperations, CancellationToken ct = default);
}
