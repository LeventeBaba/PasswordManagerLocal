using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class DeviceEnrollmentCommitRepository : IDeviceEnrollmentCommitRepository
{
    private readonly DbSet<DeviceEnrollmentCommit> _rows;
    public DeviceEnrollmentCommitRepository(AppDbContext context) => _rows = context.DeviceEnrollmentCommits;

    public Task<DeviceEnrollmentCommit?> GetByIdAsync(Guid commitId, CancellationToken ct = default) => _rows.FirstOrDefaultAsync(row => row.CommitId == commitId, ct);

    public Task<DeviceEnrollmentCommit?> GetRecoverableAsync(Guid userId, Guid targetDeviceId, Guid targetOriginInstanceId, CancellationToken ct = default) =>
        _rows.FirstOrDefaultAsync(row => row.UserId == userId && row.TargetDeviceId == targetDeviceId && row.TargetOriginInstanceId == targetOriginInstanceId &&
            row.Status != DeviceEnrollmentCommitStatus.Revoked, ct);

    public async Task<IReadOnlyList<DeviceEnrollmentCommit>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // SQLite cannot translate ORDER BY for DateTimeOffset. Enrollment rows per user
        // are bounded, so filter in SQL and sort deterministically in memory.
        var rows = await _rows
            .Where(row => row.UserId == userId)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenBy(row => row.CommitId)
            .ToList();
    }

    public Task AddAsync(DeviceEnrollmentCommit commit, CancellationToken ct = default) => _rows.AddAsync(commit, ct).AsTask();
    public void Update(DeviceEnrollmentCommit commit) => _rows.Update(commit);
}
