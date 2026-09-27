using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserSyncStateRepository : IUserSyncStateRepository
{
    private readonly DbSet<UserSyncState> _states;

    public UserSyncStateRepository(AppDbContext context)
    {
        _states = context.UserSyncStates;
    }

    public Task<UserSyncState?> GetAsync(Guid userId, CancellationToken ct = default) =>
        _states.FirstOrDefaultAsync(state => state.UserId == userId, ct);

    public Task AddAsync(UserSyncState state, CancellationToken ct = default) =>
        _states.AddAsync(state, ct).AsTask();

    public void Update(UserSyncState state) => _states.Update(state);
}
