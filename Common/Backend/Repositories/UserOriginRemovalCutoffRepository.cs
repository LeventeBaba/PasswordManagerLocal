using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserOriginRemovalCutoffRepository : IUserOriginRemovalCutoffRepository
{
    private readonly DbSet<UserOriginRemovalCutoff> _rows;
    public UserOriginRemovalCutoffRepository(AppDbContext context) => _rows = context.UserOriginRemovalCutoffs;

    public Task<UserOriginRemovalCutoff?> GetAsync(Guid userId, Guid deviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        _rows.FirstOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId && row.UserKeyEpoch == userKeyEpoch, ct);

    public async Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _rows.Where(row => row.UserId == userId).OrderBy(row => row.ResultingMembershipEpoch).ThenBy(row => row.DeviceId).ThenBy(row => row.OriginInstanceId).ThenBy(row => row.UserKeyEpoch).ToListAsync(ct);

    public async Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForOriginAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default) =>
        await _rows.Where(row => row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId).OrderBy(row => row.UserKeyEpoch).ToListAsync(ct);

    public Task AddAsync(UserOriginRemovalCutoff cutoff, CancellationToken ct = default) => _rows.AddAsync(cutoff, ct).AsTask();
}
