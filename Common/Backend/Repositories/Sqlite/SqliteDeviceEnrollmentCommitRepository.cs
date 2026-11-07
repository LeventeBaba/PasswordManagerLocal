using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteDeviceEnrollmentCommitRepository(SqliteSession session) : IDeviceEnrollmentCommitRepository
{
    private const string Select = """
        SELECT "CommitId", "AdditionOperationHash", "AdditionOperationId", "CompletedAtUtc", "CreatedAtUtc",
               "LastAttemptAtUtc", "LastError", "Status", "TargetAgreementPublicKeyHash", "TargetDeviceId",
               "TargetDeviceType", "TargetOriginInstanceId", "TargetSignPublicKeyHash", "TargetTlsCertFingerprint",
               "UserId", "Version"
        FROM "DeviceEnrollmentCommits"
        """;

    private readonly SqliteRowWriter<DeviceEnrollmentCommit> _rows = new(session, "DeviceEnrollmentCommits", 1,
    [
        (nameof(DeviceEnrollmentCommit.CommitId), row => row.CommitId),
        (nameof(DeviceEnrollmentCommit.AdditionOperationHash), row => row.AdditionOperationHash),
        (nameof(DeviceEnrollmentCommit.AdditionOperationId), row => row.AdditionOperationId),
        (nameof(DeviceEnrollmentCommit.CompletedAtUtc), row => row.CompletedAtUtc),
        (nameof(DeviceEnrollmentCommit.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(DeviceEnrollmentCommit.LastAttemptAtUtc), row => row.LastAttemptAtUtc),
        (nameof(DeviceEnrollmentCommit.LastError), row => row.LastError),
        (nameof(DeviceEnrollmentCommit.Status), row => (byte)row.Status),
        (nameof(DeviceEnrollmentCommit.TargetAgreementPublicKeyHash), row => row.TargetAgreementPublicKeyHash),
        (nameof(DeviceEnrollmentCommit.TargetDeviceId), row => row.TargetDeviceId),
        (nameof(DeviceEnrollmentCommit.TargetDeviceType), row => (byte)row.TargetDeviceType),
        (nameof(DeviceEnrollmentCommit.TargetOriginInstanceId), row => row.TargetOriginInstanceId),
        (nameof(DeviceEnrollmentCommit.TargetSignPublicKeyHash), row => row.TargetSignPublicKeyHash),
        (nameof(DeviceEnrollmentCommit.TargetTlsCertFingerprint), row => row.TargetTlsCertFingerprint),
        (nameof(DeviceEnrollmentCommit.UserId), row => row.UserId),
        (nameof(DeviceEnrollmentCommit.Version), row => row.Version)
    ], [nameof(DeviceEnrollmentCommit.Version)]);

    public async Task<DeviceEnrollmentCommit?> GetByIdAsync(Guid commitId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "CommitId" = $id; """,
            ReadForWrite, ct, ("$id", commitId))).SingleOrDefault();

    public async Task<DeviceEnrollmentCommit?> GetRecoverableAsync(
        Guid userId, Guid targetDeviceId, Guid targetOriginInstanceId, CancellationToken ct = default) =>
        (await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "TargetDeviceId" = $device
                AND "TargetOriginInstanceId" = $instance AND "Status" <> $revoked LIMIT 1;
            """, ReadForWrite, ct, ("$user", userId), ("$device", targetDeviceId), ("$instance", targetOriginInstanceId),
            ("$revoked", (byte)DeviceEnrollmentCommitStatus.Revoked))).SingleOrDefault();

    public async Task<IReadOnlyList<DeviceEnrollmentCommit>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "UserId" = $user; """, ReadForWrite, ct, ("$user", userId)))
            .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.CommitId).ToArray();

    public Task AddAsync(DeviceEnrollmentCommit commit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(commit, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(DeviceEnrollmentCommit commit) => _rows.Stage(commit, SqliteMutation.Operation.Update);

    public void DeleteRange(IEnumerable<DeviceEnrollmentCommit> commits)
    {
        foreach (var row in commits)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private DeviceEnrollmentCommit ReadForWrite(SqliteDataReader row) => _rows.Remember(new DeviceEnrollmentCommit
    {
        CommitId = row.GetGuid(0),
        AdditionOperationHash = row.Blob(1),
        AdditionOperationId = row.GetGuid(2),
        CompletedAtUtc = row.NullableUtcOffset(3),
        CreatedAtUtc = row.UtcOffset(4),
        LastAttemptAtUtc = row.UtcOffset(5),
        LastError = row.NullableText(6),
        Status = (DeviceEnrollmentCommitStatus)row.GetByte(7),
        TargetAgreementPublicKeyHash = row.Blob(8),
        TargetDeviceId = row.GetGuid(9),
        TargetDeviceType = (DeviceType)row.GetByte(10),
        TargetOriginInstanceId = row.GetGuid(11),
        TargetSignPublicKeyHash = row.Blob(12),
        TargetTlsCertFingerprint = row.GetString(13),
        UserId = row.GetGuid(14),
        Version = row.GetInt64(15)
    });
}
