using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IDeviceIdentityRepository
{
    Task<LocalDeviceIdentity?> Get(CancellationToken ct = default);
    Task Create(LocalDeviceIdentity identity, CancellationToken ct = default);
    void Update(LocalDeviceIdentity identity);
}