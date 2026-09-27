using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserControlOperationAntiEntropyService
{
    Task<UserControlOperationInventoryExchangeRequest> BuildInventoryAsync(Guid peerDeviceId, CancellationToken ct = default);

    Task<UserControlOperationInventoryExchangeReply> BuildInventoryReplyAsync(
        Guid peerDeviceId,
        UserControlOperationInventoryExchangeRequest remoteInventory,
        CancellationToken ct = default);

    IReadOnlyList<UserControlOperationRequest> FindMissingOperations(
        UserControlOperationInventoryExchangeRequest localInventory,
        IEnumerable<UserControlOperationUserInventory> remoteInventory);

    Task<IReadOnlyList<NetworkDelta>> BuildRequestedOperationDeltasAsync(
        Guid peerDeviceId,
        IEnumerable<UserControlOperationRequest> requests,
        CancellationToken ct = default);
}
