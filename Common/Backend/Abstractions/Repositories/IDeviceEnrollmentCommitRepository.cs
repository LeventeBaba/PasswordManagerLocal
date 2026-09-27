using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IDeviceEnrollmentCommitRepository
{
    Task<DeviceEnrollmentCommit?> GetByIdAsync(Guid commitId, CancellationToken ct = default);
    Task<DeviceEnrollmentCommit?> GetRecoverableAsync(Guid userId, Guid targetDeviceId, Guid targetOriginInstanceId, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceEnrollmentCommit>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task AddAsync(DeviceEnrollmentCommit commit, CancellationToken ct = default);
    void Update(DeviceEnrollmentCommit commit);
}
