using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserControlOperationAntiEntropyService
{
    Task<UserControlOperationInventoryExchangeRequest> BuildInventoryAsync(Guid peerDeviceId, CancellationToken ct = default);

    Task<UserControlOperationInventoryExchangeReply> BuildInventoryReplyAsync(
        Guid peerDeviceId,
        UserControlOperationInventoryExchangeRequest remoteInventory,
        CancellationToken ct = default);

    Task<UserControlOperationInventoryExchangeRequest?> BuildReceiptFollowUpAsync(
        Guid peerDeviceId, UserControlOperationInventoryExchangeReply remote, CancellationToken ct = default);

    Task AcceptReceiptReplyAsync(Guid peerDeviceId, UserControlOperationInventoryExchangeRequest request,
        UserControlOperationInventoryExchangeReply reply, CancellationToken ct = default);

    IReadOnlyList<UserControlOperationRequest> FindMissingOperations(
        UserControlOperationInventoryExchangeRequest localInventory,
        IEnumerable<UserControlOperationUserInventory> remoteInventory);

    Task<IReadOnlyList<NetworkDelta>> BuildRequestedOperationDeltasAsync(
        Guid peerDeviceId,
        IEnumerable<UserControlOperationRequest> requests,
        CancellationToken ct = default);
}
