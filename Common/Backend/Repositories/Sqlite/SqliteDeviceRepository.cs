using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteDeviceRepository(SqliteSession session) : IDeviceRepository
{
    internal const string Projection = """
        d."Id", d."BlockedAt", d."BlockedReason", d."DeviceType", d."IntegrityHash", d."InvalidSyncAttemptCount",
        d."IsBlocked", d."IsTrusted", d."LastInvalidSyncAttemptAt", d."LastKnownHash", d."LastModifiedAt",
        d."LastSeen", d."LastSync", d."PublicKey", d."SignPublicKey", d."SignPublicKeyHash", d."TlsCertFingerprint"
        """;
    private const int FieldCount = 17;
    private const string Select = $"""SELECT {Projection} FROM "Devices" d""";
    private const string SelectWithLinks = $"""
        SELECT {Projection}, {SqliteUserDeviceRepository.Projection}
        FROM "Devices" d LEFT JOIN "UserDevices" ud ON ud."DeviceId" = d."Id"
        """;

    private readonly Dictionary<Guid, Device> _stagedDevices = [];

    private readonly SqliteRowWriter<Device> _rows = new(session, "Devices", 1,
    [
        (nameof(Device.Id), row => row.Id),
        (nameof(Device.BlockedAt), row => row.BlockedAt),
        (nameof(Device.BlockedReason), row => row.BlockedReason),
        (nameof(Device.DeviceType), row => (byte)row.DeviceType),
        (nameof(Device.IntegrityHash), row => row.IntegrityHash),
        (nameof(Device.InvalidSyncAttemptCount), row => row.InvalidSyncAttemptCount),
        (nameof(Device.IsBlocked), row => row.IsBlocked),
        (nameof(Device.IsTrusted), row => row.IsTrusted),
        (nameof(Device.LastInvalidSyncAttemptAt), row => row.LastInvalidSyncAttemptAt),
        (nameof(Device.LastKnownHash), row => row.LastKnownHash),
        (nameof(Device.LastModifiedAt), row => row.LastModifiedAt),
        (nameof(Device.LastSeen), row => row.LastSeen),
        (nameof(Device.LastSync), row => row.LastSync),
        (nameof(Device.PublicKey), row => row.PublicKey),
        (nameof(Device.SignPublicKey), row => row.SignPublicKey),
        (nameof(Device.SignPublicKeyHash), row => row.SignPublicKeyHash),
        (nameof(Device.TlsCertFingerprint), row => row.TlsCertFingerprint)
    ], concurrencyColumns: [nameof(Device.IntegrityHash)]);

    public async Task<IReadOnlyList<Device>> ListAllAsync(CancellationToken ct = default) =>
        await session.ReadAsync(Select, ReadDetached, ct);

    public async Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE d."Id" = $id;""", Read, ct, ("$id", id))).SingleOrDefault();

    public async Task<(bool found, Device? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await GetByIdAsync(id, ct);
        return (row is not null, row);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "Devices" WHERE "Id" = $id""", ct, ("$id", id));

    public Task AddAsync(Device entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        StageWrite(entity, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(Device entity) => StageWrite(entity, SqliteMutation.Operation.Update);

    public void Delete(Device entity)
    {
        _rows.Stage(entity, SqliteMutation.Operation.Delete);
        _stagedDevices[entity.Id] = entity;
    }

    public Device? GetObservationWriteTarget(Device observed)
    {
        // The authenticated source and an incoming delta's target can be the same row.
        // Apply the observation to the row already staged by the delta in this scope.
        if (_stagedDevices.TryGetValue(observed.Id, out var staged) &&
            session.GetPendingOperation(staged) is { } operation)
        {
            return operation == SqliteMutation.Operation.Delete ? null : staged;
        }
        return observed;
    }

    public async Task<IReadOnlyList<Device>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(ids, Select + """ WHERE d."Id" IN ({ids});""", Read, ct);

    public async Task<IReadOnlyList<Device>> ListTrustedUnblockedWithPendingQueueAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select}
            WHERE d."IsTrusted" = 1 AND d."IsBlocked" = 0 AND d."TlsCertFingerprint" <> ''
              AND EXISTS (SELECT 1 FROM "SyncQueueItems" q WHERE q."DeviceId" = d."Id" AND q."ProcessedAt" IS NULL);
            """, ReadDetached, ct);

    public async Task<Device?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE d."Id" = $id;""", ReadDetached, ct, ("$id", id))).SingleOrDefault();

    public async Task<Device?> GetByIdWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        (await ListWithLinksAsync($"""{SelectWithLinks} WHERE d."Id" = $id;""", true, ct, ("$id", id))).SingleOrDefault();

    public async Task<Device?> GetByIdAsNoTrackingWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        (await ListWithLinksAsync($"""{SelectWithLinks} WHERE d."Id" = $id;""", false, ct, ("$id", id))).SingleOrDefault();

    public async Task<IReadOnlyList<Guid>> ListActiveUserIdsAsync(Guid deviceId, CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT DISTINCT ud."UserId" FROM "UserDevices" ud JOIN "Devices" d ON d."Id" = ud."DeviceId"
            WHERE ud."DeviceId" = $id AND ud."IsDeleted" = 0;
            """, row => row.GetGuid(0), ct, ("$id", deviceId));

    public async Task<IReadOnlyList<Device>> ListBySigningKeyHashAsync(byte[] hash, CancellationToken ct = default) =>
        await session.ReadAsync($"""{Select} WHERE d."SignPublicKeyHash" = $hash;""", Read, ct, ("$hash", hash));

    public async Task<Device?> GetByNormalizedFingerprintAsync(string fingerprint, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE d."TlsCertFingerprint" = $fingerprint;""",
            Read, ct, ("$fingerprint", fingerprint))).SingleOrDefault();

    public async Task<Device?> GetByNormalizedFingerprintWithUserDevicesAsync(string fingerprint, CancellationToken ct = default) =>
        (await ListWithLinksAsync($"""{SelectWithLinks} WHERE d."TlsCertFingerprint" = $fingerprint;""",
            true, ct, ("$fingerprint", fingerprint))).SingleOrDefault();

    public async Task<IReadOnlyList<Device>> ListLocalIdentityCandidatesAsync(
        Guid localDeviceId, byte[] signingKeyHash, string fingerprint, CancellationToken ct = default) =>
        await ListWithLinksAsync($"""
            {SelectWithLinks}
            WHERE d."Id" = $id
               OR ($hash IS NOT NULL AND d."SignPublicKeyHash" = $hash)
               OR ($fingerprint IS NOT NULL AND d."TlsCertFingerprint" = $fingerprint);
            """, true, ct, ("$id", localDeviceId), ("$hash", signingKeyHash.Length == 0 ? null : signingKeyHash),
            ("$fingerprint", fingerprint.Length == 0 ? null : fingerprint));

    public async Task<IReadOnlyList<Device>> ListByIdsWithUserDevicesAsNoTrackingAsync(
        IReadOnlyCollection<Guid> deviceIds, Guid excludedDeviceId, CancellationToken ct = default)
    {
        var joined = await session.ReadByIdsAsync(deviceIds,
            SelectWithLinks + """ WHERE d."Id" <> $excluded AND d."Id" IN ({ids});""",
            ReadWithLink, ct, ("$excluded", excludedDeviceId));
        return AttachLinks(joined, remember: false);
    }

    public async Task<IReadOnlyList<Device>> ListTrustedUnblockedAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""{Select} WHERE d."IsTrusted" = 1 AND d."IsBlocked" = 0;""", ReadDetached, ct);

    private void StageWrite(Device device, SqliteMutation.Operation operation)
    {
        device.LastSeen = UtcDateTimeUtil.ToUtc(device.LastSeen);
        device.LastSync = UtcDateTimeUtil.ToUtc(device.LastSync);
        device.LastModifiedAt = UtcDateTimeUtil.ToUtc(device.LastModifiedAt);
        device.BlockedAt = UtcDateTimeUtil.ToUtc(device.BlockedAt);
        device.LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(device.LastInvalidSyncAttemptAt);
        device.GenerateIntegrityHash();
        _rows.Stage(device, operation);
        _stagedDevices[device.Id] = device;
    }

    private Device Read(SqliteDataReader row) => _rows.Remember(ReadRow(row, 0));
    private static Device ReadDetached(SqliteDataReader row) => ReadRow(row, 0);

    internal static Device ReadRow(SqliteDataReader row, int offset) => new()
    {
        Id = row.GetGuid(offset),
        BlockedAt = row.NullableUtcOffset(offset + 1),
        BlockedReason = row.NullableText(offset + 2),
        DeviceType = (DeviceType)row.GetByte(offset + 3),
        IntegrityHash = row.Blob(offset + 4),
        InvalidSyncAttemptCount = row.GetInt32(offset + 5),
        IsBlocked = row.GetBoolean(offset + 6),
        IsTrusted = row.GetBoolean(offset + 7),
        LastInvalidSyncAttemptAt = row.NullableUtcOffset(offset + 8),
        LastKnownHash = row.Blob(offset + 9),
        LastModifiedAt = row.UtcOffset(offset + 10),
        LastSeen = row.UtcDateTime(offset + 11),
        LastSync = row.UtcDateTime(offset + 12),
        PublicKey = row.Blob(offset + 13),
        SignPublicKey = row.Blob(offset + 14),
        SignPublicKeyHash = row.Blob(offset + 15),
        TlsCertFingerprint = row.GetString(offset + 16)
    };

    private static (Device Device, UserDevice? Link) ReadWithLink(SqliteDataReader row) =>
        (ReadRow(row, 0), row.IsDBNull(FieldCount) ? null : SqliteUserDeviceRepository.ReadRow(row, FieldCount));

    private async Task<List<Device>> ListWithLinksAsync(
        string sql, bool remember, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        AttachLinks(await session.ReadAsync(sql, ReadWithLink, ct, parameters), remember);

    private List<Device> AttachLinks(IEnumerable<(Device Device, UserDevice? Link)> joined, bool remember)
    {
        var devices = new Dictionary<Guid, Device>();
        foreach (var (row, link) in joined)
        {
            if (!devices.TryGetValue(row.Id, out var device))
            {
                device = remember ? _rows.Remember(row) : row;
                devices.Add(device.Id, device);
            }
            if (link is not null)
            {
                link.Device = device;
                device.UserDevices.Add(link);
            }
        }
        return devices.Values.ToList();
    }
}
