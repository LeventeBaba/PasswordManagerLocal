using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserRepository : GenericRepositoryBase<User>, IUserRepository
{
    private readonly DbSet<UserLoginIdentityState> _loginIdentities;

    public UserRepository(AppDbContext db) : base(db.Users)
    {
        _loginIdentities = db.UserLoginIdentityStates;
    }

    public override Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking().AnyAsync(u => u.UId == id, ct);

    public override Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(u => u.UId == id, ct);

    public async Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking().Select(user => user.UId).ToListAsync(ct);

    public async Task<IReadOnlyList<UserLoginLookupData>> ListLoginLookupDataAsync(CancellationToken ct = default) =>
        await _loginIdentities.AsNoTracking()
            .Select(identity => new UserLoginLookupData
            {
                UId = identity.UserId,
                UsernameSalt = identity.UsernameSalt,
                UsernameHash = identity.UsernameHash,
                Status = identity.Status,
                KeyEpoch = identity.KeyEpoch,
                MembershipEpoch = identity.MembershipEpoch
            })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserLoginIdentityState>> ListLoginIdentityStatesAsync(CancellationToken ct = default) =>
        await _loginIdentities.AsNoTracking().ToListAsync(ct);

    public Task<UserLoginIdentityState?> GetLoginIdentityStateAsync(Guid userId, CancellationToken ct = default) =>
        _loginIdentities.FirstOrDefaultAsync(identity => identity.UserId == userId, ct);

    public Task AddLoginIdentityStateAsync(UserLoginIdentityState state, CancellationToken ct = default) =>
        _loginIdentities.AddAsync(state, ct).AsTask();

    public void UpdateLoginIdentityState(UserLoginIdentityState state) => _loginIdentities.Update(state);

    public void DeleteLoginIdentityState(UserLoginIdentityState state) => _loginIdentities.Remove(state);

    public Task<User?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(u => u.UId == id, ct);

    public async Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        return await Set.Where(u => ids.Contains(u.UId)).ToListAsync(ct);
    }

    public Task<User?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default) =>
        Set
            .Include(u => u.Groups)
            .Include(u => u.UserDevices)
            .Include(u => u.LocalUserDevices)
            .FirstOrDefaultAsync(u => u.UId == id, ct);

    public Task<User?> GetByIdAsNoTrackingWithRelationsAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking()
            .Include(u => u.Groups)
            .Include(u => u.UserDevices)
            .Include(u => u.LocalUserDevices)
            .FirstOrDefaultAsync(u => u.UId == id, ct);


    public Task UpdateSavedKeyAsync(Guid id, byte[]? savedKey, CancellationToken ct = default) =>
        Set.Where(user => user.UId == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.SavedKey, savedKey),
                ct);

    public async Task<IReadOnlyList<User>> GetAllRememberMeEnabledUsersAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(u => u.SavedKey != null)
            .ToListAsync(ct);
}
