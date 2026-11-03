using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserCanonicalCheckpointRepository : IUserCanonicalCheckpointRepository
{
    private readonly Dictionary<Guid, UserCanonicalCheckpoint> _rows = [];
    public Task<UserCanonicalCheckpoint?> GetAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_rows.GetValueOrDefault(userId));
    public Task AddAsync(UserCanonicalCheckpoint checkpoint, CancellationToken ct = default)
    {
        _rows.Add(checkpoint.UserId, checkpoint);
        return Task.CompletedTask;
    }
    public void Update(UserCanonicalCheckpoint checkpoint) => _rows[checkpoint.UserId] = checkpoint;
    public void Delete(UserCanonicalCheckpoint checkpoint) => _rows.Remove(checkpoint.UserId);
}
