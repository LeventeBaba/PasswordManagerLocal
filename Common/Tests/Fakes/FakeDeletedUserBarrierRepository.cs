using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeletedUserBarrierRepository : IDeletedUserBarrierRepository
{
    private readonly Dictionary<Guid, DeletedUserBarrier> _rows = [];

    public Task<DeletedUserBarrier?> GetAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_rows.GetValueOrDefault(userId));

    public Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_rows.ContainsKey(userId));

    public Task<IReadOnlyList<DeletedUserBarrier>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeletedUserBarrier>>(_rows.Values.OrderBy(row => row.UserId).ToList());

    public Task AddAsync(DeletedUserBarrier barrier, CancellationToken ct = default)
    {
        if (!_rows.TryAdd(barrier.UserId, barrier))
            throw new InvalidOperationException("A deletion barrier already exists for this user.");
        return Task.CompletedTask;
    }

    public void Update(DeletedUserBarrier barrier) => _rows[barrier.UserId] = barrier;
}
