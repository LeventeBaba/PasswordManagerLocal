using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserControlOperationRepository : IUserControlOperationRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<UserControlOperation> _operations;

    public UserControlOperationRepository(AppDbContext context)
    {
        _context = context;
        _operations = context.UserControlOperations;
    }

    public Task<UserControlOperation?> GetByIdAsync(Guid operationId, CancellationToken ct = default) =>
        _operations.FirstOrDefaultAsync(operation => operation.OperationId == operationId, ct);

    public Task<UserControlOperation?> GetByOriginSequenceAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long originSequence,
        CancellationToken ct = default) =>
        _operations.FirstOrDefaultAsync(operation =>
            operation.UserId == userId &&
            operation.OriginDeviceId == originDeviceId &&
            operation.OriginInstanceId == originInstanceId &&
            operation.OriginSequence == originSequence, ct);

    public async Task<IReadOnlyList<UserControlOperation>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // SQLite cannot translate ORDER BY for DateTimeOffset. Keep filtering in the
        // database, then apply the deterministic control-operation order in memory.
        var operations = await _operations
            .Where(operation => operation.UserId == userId)
            .ToListAsync(ct);

        return operations
            .OrderBy(operation => operation.CreatedAtUtc)
            .ThenBy(operation => operation.OriginDeviceId)
            .ThenBy(operation => operation.OriginInstanceId)
            .ThenBy(operation => operation.OriginSequence)
            .ToList();
    }

    public async Task<IReadOnlyList<UserControlOperation>> ListAllRelayableAsync(CancellationToken ct = default) =>
        await _operations
            .Where(operation => operation.Status != UserControlOperationStatus.Rejected)
            .OrderBy(operation => operation.UserId)
            .ThenBy(operation => operation.OriginDeviceId)
            .ThenBy(operation => operation.OriginInstanceId)
            .ThenBy(operation => operation.OriginSequence)
            .ToListAsync(ct);

    public Task<bool> HasAppliedAccountDeletionAsync(CancellationToken ct = default) =>
        _operations.AsNoTracking().AnyAsync(
            operation => operation.OperationType == UserControlOperationType.AccountDeletion &&
                         operation.Status == UserControlOperationStatus.Applied,
            ct);

    public async Task<IReadOnlyList<Guid>> ListAppliedAccountDeletionUserIdsAsync(CancellationToken ct = default) =>
        await _operations.AsNoTracking()
            .Where(operation => operation.OperationType == UserControlOperationType.AccountDeletion &&
                                operation.Status == UserControlOperationStatus.Applied)
            .Select(operation => operation.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserControlOperation>> ListPendingAsync(CancellationToken ct = default)
    {
        // See ListForUserAsync: DateTimeOffset ordering must happen client-side for SQLite.
        var operations = await _operations.AsNoTracking()
            .Where(operation => operation.Status == UserControlOperationStatus.StoredPending)
            .ToListAsync(ct);

        return operations
            .OrderBy(operation => operation.ReceivedAtUtc)
            .ThenBy(operation => operation.OperationId)
            .ToList();
    }

    public async Task<IReadOnlyList<UserControlOperation>> ListKeyTransitionsFromAsync(
        Guid userId,
        long previousKeyEpoch,
        CancellationToken ct = default) =>
        await _operations
            .Where(operation =>
                operation.UserId == userId &&
                operation.OperationType == UserControlOperationType.KeyEpochReplacement &&
                operation.PreviousKeyEpoch == previousKeyEpoch &&
                operation.Status != UserControlOperationStatus.Rejected)
            .OrderBy(operation => operation.OperationId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserControlOperation>> ListMembershipTransitionsFromAsync(
        Guid userId,
        long previousMembershipEpoch,
        CancellationToken ct = default) =>
        await _operations
            .Where(operation => operation.UserId == userId &&
                (operation.OperationType == UserControlOperationType.DeviceAddition || operation.OperationType == UserControlOperationType.DeviceRemoval) &&
                operation.PreviousMembershipEpoch == previousMembershipEpoch &&
                operation.Status != UserControlOperationStatus.Rejected)
            .OrderBy(operation => operation.OperationId)
            .ToListAsync(ct);

    public async Task<long> GetHighestOriginSequenceAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        CancellationToken ct = default) =>
        await _operations
            .Where(operation => operation.UserId == userId && operation.OriginDeviceId == originDeviceId && operation.OriginInstanceId == originInstanceId)
            .Select(operation => (long?)operation.OriginSequence)
            .MaxAsync(ct) ?? 0;

    public Task AddAsync(UserControlOperation operation, CancellationToken ct = default) =>
        _operations.AddAsync(operation, ct).AsTask();

    public void Update(UserControlOperation operation) => _operations.Update(operation);

    public void Detach(UserControlOperation operation) =>
        _context.Entry(operation).State = EntityState.Detached;
}
