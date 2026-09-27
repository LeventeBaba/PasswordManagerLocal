using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserControlOperationRepository : IUserControlOperationRepository
{
    private readonly List<UserControlOperation> _rows = [];

    public Task<UserControlOperation?> GetByIdAsync(Guid operationId, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.OperationId == operationId));

    public Task<UserControlOperation?> GetByOriginSequenceAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long originSequence, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.UserId == userId && row.OriginDeviceId == originDeviceId && row.OriginInstanceId == originInstanceId && row.OriginSequence == originSequence));

    public Task<IReadOnlyList<UserControlOperation>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserControlOperation>>(_rows.Where(row => row.UserId == userId).ToList());

    public Task<IReadOnlyList<UserControlOperation>> ListAllRelayableAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserControlOperation>>(_rows.Where(row => row.Status != UserControlOperationStatus.Rejected).ToList());

    public Task<bool> HasAppliedAccountDeletionAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Any(row => row.OperationType == UserControlOperationType.AccountDeletion && row.Status == UserControlOperationStatus.Applied));

    public Task<IReadOnlyList<Guid>> ListAppliedAccountDeletionUserIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(_rows
            .Where(row => row.OperationType == UserControlOperationType.AccountDeletion && row.Status == UserControlOperationStatus.Applied)
            .Select(row => row.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToList());

    public Task<IReadOnlyList<UserControlOperation>> ListPendingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserControlOperation>>(_rows.Where(row => row.Status == UserControlOperationStatus.StoredPending).ToList());

    public Task<IReadOnlyList<UserControlOperation>> ListKeyTransitionsFromAsync(Guid userId, long previousKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserControlOperation>>(_rows.Where(row => row.UserId == userId && row.OperationType == UserControlOperationType.KeyEpochReplacement && row.PreviousKeyEpoch == previousKeyEpoch && (row.Status is UserControlOperationStatus.StoredPending or UserControlOperationStatus.Applied)).ToList());

    public Task<IReadOnlyList<UserControlOperation>> ListMembershipTransitionsFromAsync(Guid userId, long previousMembershipEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserControlOperation>>(_rows.Where(row => row.UserId == userId && (row.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval) && row.PreviousMembershipEpoch == previousMembershipEpoch && (row.Status is UserControlOperationStatus.StoredPending or UserControlOperationStatus.Applied)).ToList());

    public Task<long> GetHighestOriginSequenceAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default) =>
        Task.FromResult(_rows.Where(row => row.UserId == userId && row.OriginDeviceId == originDeviceId && row.OriginInstanceId == originInstanceId).Select(row => row.OriginSequence).DefaultIfEmpty(0).Max());

    public Task AddAsync(UserControlOperation operation, CancellationToken ct = default)
    {
        _rows.Add(operation);
        return Task.CompletedTask;
    }

    public void Update(UserControlOperation operation) { }
    public void Detach(UserControlOperation operation) => _rows.Remove(operation);
}
