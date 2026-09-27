using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserOriginRemovalCutoffRepository : IUserOriginRemovalCutoffRepository
{
    private readonly List<UserOriginRemovalCutoff> _rows = [];

    public Task<UserOriginRemovalCutoff?> GetAsync(Guid userId, Guid deviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId && row.UserKeyEpoch == userKeyEpoch));

    public Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserOriginRemovalCutoff>>(_rows.Where(row => row.UserId == userId).ToList());

    public Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForOriginAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserOriginRemovalCutoff>>(_rows.Where(row => row.UserId == userId && row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId).ToList());

    public Task AddAsync(UserOriginRemovalCutoff cutoff, CancellationToken ct = default)
    {
        _rows.Add(cutoff);
        return Task.CompletedTask;
    }
}
