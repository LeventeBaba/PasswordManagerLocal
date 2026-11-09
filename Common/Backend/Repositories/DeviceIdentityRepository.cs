using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class DeviceIdentityRepository(SqliteSession session) : IDeviceIdentityRepository
{
    internal const string Projection = """
        li."Id", li."AgreementPrivateKeyBlob", li."CreatedAt", li."DeviceType", li."IntegrityHash",
        li."IsSyncOn", li."OriginInstanceId", li."PFXCertificate", li."SignPrivateKeyBlob"
        """;

    private readonly SqliteRowWriter<LocalDeviceIdentity> _rows = new(session, "LocalDeviceIdentity", 1,
    [
        (nameof(LocalDeviceIdentity.Id), row => row.Id),
        (nameof(LocalDeviceIdentity.AgreementPrivateKeyBlob), row => row.AgreementPrivateKeyBlob),
        (nameof(LocalDeviceIdentity.CreatedAt), row => row.CreatedAt),
        (nameof(LocalDeviceIdentity.DeviceType), row => (byte)row.DeviceType),
        (nameof(LocalDeviceIdentity.IntegrityHash), row => row.IntegrityHash),
        (nameof(LocalDeviceIdentity.IsSyncOn), row => row.IsSyncOn),
        (nameof(LocalDeviceIdentity.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(LocalDeviceIdentity.PFXCertificate), row => row.PFXCertificate),
        (nameof(LocalDeviceIdentity.SignPrivateKeyBlob), row => row.SignPrivateKeyBlob)
    ]);

    public async Task<LocalDeviceIdentity?> Get(CancellationToken ct = default)
    {
        var rows = await session.ReadAsync(
            $"""SELECT {Projection} FROM "LocalDeviceIdentity" li WHERE li."SingletonKey" = 1;""",
            row => _rows.Remember(ReadRow(row, 0)), ct);
        return rows.SingleOrDefault();
    }

    public Task Create(LocalDeviceIdentity identity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(identity, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(LocalDeviceIdentity identity) => _rows.Stage(identity, SqliteMutation.Operation.Update);

    internal static LocalDeviceIdentity ReadRow(SqliteDataReader row, int offset) => new()
    {
        Id = row.GetGuid(offset),
        AgreementPrivateKeyBlob = row.Blob(offset + 1),
        CreatedAt = row.UtcOffset(offset + 2),
        DeviceType = (DeviceType)row.GetByte(offset + 3),
        IntegrityHash = row.Blob(offset + 4),
        IsSyncOn = row.GetBoolean(offset + 5),
        OriginInstanceId = row.GetGuid(offset + 6),
        PFXCertificate = row.Blob(offset + 7),
        SignPrivateKeyBlob = row.Blob(offset + 8)
    };
}
