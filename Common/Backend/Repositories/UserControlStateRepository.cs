using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserControlStateRepository : IUserControlStateRepository
{
    private readonly DbSet<UserControlState> _states;

    public UserControlStateRepository(AppDbContext context)
    {
        _states = context.UserControlStates;
    }

    public Task<UserControlState?> GetAsync(Guid userId, CancellationToken ct = default) =>
        _states.FirstOrDefaultAsync(state => state.UserId == userId, ct);

    public Task AddAsync(UserControlState state, CancellationToken ct = default) =>
        _states.AddAsync(state, ct).AsTask();

    public void Update(UserControlState state) => _states.Update(state);
}
