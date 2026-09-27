using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeviceEnrollmentCommitRepository : IDeviceEnrollmentCommitRepository
{
    private readonly List<DeviceEnrollmentCommit> _rows = [];

    public Task<DeviceEnrollmentCommit?> GetByIdAsync(Guid commitId, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.CommitId == commitId));

    public Task<DeviceEnrollmentCommit?> GetRecoverableAsync(Guid userId, Guid targetDeviceId, Guid targetOriginInstanceId, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.UserId == userId && row.TargetDeviceId == targetDeviceId && row.TargetOriginInstanceId == targetOriginInstanceId && row.Status != DeviceEnrollmentCommitStatus.Revoked));

    public Task<IReadOnlyList<DeviceEnrollmentCommit>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceEnrollmentCommit>>(_rows.Where(row => row.UserId == userId).ToList());

    public Task AddAsync(DeviceEnrollmentCommit commit, CancellationToken ct = default)
    {
        _rows.Add(commit);
        return Task.CompletedTask;
    }

    public void Update(DeviceEnrollmentCommit commit) { }
}
