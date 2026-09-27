using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserControlOperationRepository
{
    Task<UserControlOperation?> GetByIdAsync(Guid operationId, CancellationToken ct = default);
    Task<UserControlOperation?> GetByOriginSequenceAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long originSequence, CancellationToken ct = default);
    Task<IReadOnlyList<UserControlOperation>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserControlOperation>> ListAllRelayableAsync(CancellationToken ct = default);
    Task<bool> HasAppliedAccountDeletionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListAppliedAccountDeletionUserIdsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserControlOperation>> ListPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserControlOperation>> ListKeyTransitionsFromAsync(Guid userId, long previousKeyEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserControlOperation>> ListMembershipTransitionsFromAsync(Guid userId, long previousMembershipEpoch, CancellationToken ct = default);
    Task<long> GetHighestOriginSequenceAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default);
    Task AddAsync(UserControlOperation operation, CancellationToken ct = default);
    void Update(UserControlOperation operation);
    void Detach(UserControlOperation operation);
}
