using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserControlStateRepository : IUserControlStateRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<UserControlState> _states;

    public UserControlStateRepository(AppDbContext context)
    {
        _context = context;
        _states = context.UserControlStates;
    }

    public Task<UserControlState?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _states.FindAsync([userId], ct).AsTask();
    }

    public Task AddAsync(UserControlState state, CancellationToken ct = default) =>
        _states.AddAsync(state, ct).AsTask();

    public void Update(UserControlState state)
    {
        if (_context.Entry(state).State == EntityState.Detached)
            _states.Update(state);
    }
}
