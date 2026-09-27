using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserControlStateRepository
{
    Task<UserControlState?> GetAsync(Guid userId, CancellationToken ct = default);
    Task AddAsync(UserControlState state, CancellationToken ct = default);
    void Update(UserControlState state);
}
