using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserMembershipAuthorizationRepository(SqliteSession session) : IUserMembershipAuthorizationRepository
{
    private const string Select = """
        SELECT "AuthorizationId", "AdditionOperationHash", "AdditionOperationId", "AgreementPublicKeyHash",
               "CreatedAtUtc", "DeviceId", "DeviceType", "EndedAtUtc", "EndedMembershipEpoch", "IsActive",
               "IsGenesis", "MaximumKeyEpoch", "MinimumKeyEpoch", "OriginInstanceId", "RemovalOperationHash",
               "RemovalOperationId", "SignPublicKey", "SignPublicKeyHash", "StartedMembershipEpoch",
               "TlsCertFingerprint", "UserId", "Version"
        FROM "UserMembershipAuthorizations"
        """;
    private const string Installation = """
        "UserId" = $user AND "DeviceId" = $device AND "OriginInstanceId" = $instance
        """;
    private const string IdentitySelect = """
        SELECT a."UserId", a."DeviceId", a."SignPublicKeyHash", a."TlsCertFingerprint",
               d."SignPublicKeyHash", d."TlsCertFingerprint"
        FROM "UserMembershipAuthorizations" a INNER JOIN "Devices" d ON d."Id" = a."DeviceId"
        """;

    private readonly SqliteRowWriter<UserMembershipAuthorization> _rows = new(session, "UserMembershipAuthorizations", 1,
    [
        (nameof(UserMembershipAuthorization.AuthorizationId), row => row.AuthorizationId),
        (nameof(UserMembershipAuthorization.AdditionOperationHash), row => row.AdditionOperationHash),
        (nameof(UserMembershipAuthorization.AdditionOperationId), row => row.AdditionOperationId),
        (nameof(UserMembershipAuthorization.AgreementPublicKeyHash), row => row.AgreementPublicKeyHash),
        (nameof(UserMembershipAuthorization.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserMembershipAuthorization.DeviceId), row => row.DeviceId),
        (nameof(UserMembershipAuthorization.DeviceType), row => (byte)row.DeviceType),
        (nameof(UserMembershipAuthorization.EndedAtUtc), row => row.EndedAtUtc),
        (nameof(UserMembershipAuthorization.EndedMembershipEpoch), row => row.EndedMembershipEpoch),
        (nameof(UserMembershipAuthorization.IsActive), row => row.IsActive),
        (nameof(UserMembershipAuthorization.IsGenesis), row => row.IsGenesis),
        (nameof(UserMembershipAuthorization.MaximumKeyEpoch), row => row.MaximumKeyEpoch),
        (nameof(UserMembershipAuthorization.MinimumKeyEpoch), row => row.MinimumKeyEpoch),
        (nameof(UserMembershipAuthorization.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserMembershipAuthorization.RemovalOperationHash), row => row.RemovalOperationHash),
        (nameof(UserMembershipAuthorization.RemovalOperationId), row => row.RemovalOperationId),
        (nameof(UserMembershipAuthorization.SignPublicKey), row => row.SignPublicKey),
        (nameof(UserMembershipAuthorization.SignPublicKeyHash), row => row.SignPublicKeyHash),
        (nameof(UserMembershipAuthorization.StartedMembershipEpoch), row => row.StartedMembershipEpoch),
        (nameof(UserMembershipAuthorization.TlsCertFingerprint), row => row.TlsCertFingerprint),
        (nameof(UserMembershipAuthorization.UserId), row => row.UserId),
        (nameof(UserMembershipAuthorization.Version), row => row.Version)
    ], [nameof(UserMembershipAuthorization.Version)]);

    public async Task<UserMembershipAuthorization?> GetByIdAsync(Guid authorizationId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "AuthorizationId" = $id; """,
            ReadForWrite, ct, ("$id", authorizationId))).SingleOrDefault();

    public async Task<UserMembershipAuthorization?> GetForSignedEpochAsync(
        Guid userId, Guid deviceId, Guid originInstanceId, long membershipEpoch, CancellationToken ct = default) =>
        (await session.ReadAsync($"""
            {Select} WHERE {Installation} AND "StartedMembershipEpoch" <= $epoch
                AND ("EndedMembershipEpoch" IS NULL OR $epoch < "EndedMembershipEpoch") LIMIT 1;
            """, ReadForWrite, ct, ("$user", userId), ("$device", deviceId), ("$instance", originInstanceId),
            ("$epoch", membershipEpoch))).SingleOrDefault();

    public async Task<UserMembershipAuthorization?> GetActiveAsync(
        Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE {Installation} AND "IsActive" = 1; """,
            ReadForWrite, ct, ("$user", userId), ("$device", deviceId), ("$instance", originInstanceId))).SingleOrDefault();

    public async Task<IReadOnlyList<UserMembershipAuthorization>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user ORDER BY "StartedMembershipEpoch", "DeviceId", "OriginInstanceId";
            """, ReadForWrite, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserMembershipAuthorization>> ListActiveForDeviceAsync(
        Guid userId, Guid deviceId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "DeviceId" = $device AND "IsActive" = 1 ORDER BY "OriginInstanceId";
            """, ReadForWrite, ct, ("$user", userId), ("$device", deviceId));

    public async Task<IReadOnlyList<Guid>> ListUserIdsForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {IdentitySelect} WHERE a."DeviceId" = $device; """,
            ReadIdentityMatch, ct, ("$device", deviceId)))
            .Where(row => row.Matches).Select(row => row.UserId).Distinct().OrderBy(id => id).ToArray();

    public async Task<IReadOnlyList<Guid>> ListDeviceIdsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        (await session.ReadByIdsAsync(userIds, IdentitySelect + """ WHERE a."UserId" IN ({ids}); """, ReadIdentityMatch, ct))
            .Where(row => row.Matches).Select(row => row.DeviceId).Distinct().OrderBy(id => id).ToArray();

    public async Task<bool> HasHistoricalAuthorizationAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {IdentitySelect} WHERE a."UserId" = $user AND a."DeviceId" = $device; """,
            ReadIdentityMatch, ct, ("$user", userId), ("$device", deviceId))).Any(row => row.Matches);

    private static (Guid UserId, Guid DeviceId, bool Matches) ReadIdentityMatch(SqliteDataReader row) =>
        (row.GetGuid(0), row.GetGuid(1), MembershipDeviceIdentity.Matches(
            row.Blob(2), row.GetString(3), row.Blob(4), row.GetString(5)));

    public Task AddAsync(UserMembershipAuthorization authorization, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(authorization, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserMembershipAuthorization authorization) => _rows.Stage(authorization, SqliteMutation.Operation.Update);

    public void DeleteRange(IEnumerable<UserMembershipAuthorization> authorizations)
    {
        foreach (var row in authorizations)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private UserMembershipAuthorization ReadForWrite(SqliteDataReader row) => _rows.Remember(new UserMembershipAuthorization
    {
        AuthorizationId = row.GetGuid(0),
        AdditionOperationHash = row.NullableBlob(1),
        AdditionOperationId = row.NullableGuid(2),
        AgreementPublicKeyHash = row.Blob(3),
        CreatedAtUtc = row.UtcOffset(4),
        DeviceId = row.GetGuid(5),
        DeviceType = (DeviceType)row.GetByte(6),
        EndedAtUtc = row.NullableUtcOffset(7),
        EndedMembershipEpoch = row.NullableInt64(8),
        IsActive = row.GetBoolean(9),
        IsGenesis = row.GetBoolean(10),
        MaximumKeyEpoch = row.NullableInt64(11),
        MinimumKeyEpoch = row.GetInt64(12),
        OriginInstanceId = row.GetGuid(13),
        RemovalOperationHash = row.NullableBlob(14),
        RemovalOperationId = row.NullableGuid(15),
        SignPublicKey = row.Blob(16),
        SignPublicKeyHash = row.Blob(17),
        StartedMembershipEpoch = row.GetInt64(18),
        TlsCertFingerprint = row.GetString(19),
        UserId = row.GetGuid(20),
        Version = row.GetInt64(21)
    });
}
