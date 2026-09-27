using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface ILocalUserDeviceRepository
{
    Task<LocalUserDevice?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<bool> IsSyncOnAsync(Guid userId, CancellationToken ct = default);
    Task<bool> AnySyncOnAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListSyncOnUserIdsAsync(CancellationToken ct = default);
    Task AddAsync(LocalUserDevice localUserDevice, CancellationToken ct = default);
    void Update(LocalUserDevice localUserDevice);
    void Delete(LocalUserDevice localUserDevice);
}
