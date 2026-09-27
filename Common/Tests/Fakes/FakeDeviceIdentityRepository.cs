using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeviceIdentityRepository : IDeviceIdentityRepository
{
    private LocalDeviceIdentity? _identity;

    public int CreateCalls { get; private set; }
    public int UpdateCalls { get; private set; }

    public Task<LocalDeviceIdentity?> Get(CancellationToken ct = default) =>
        Task.FromResult(_identity is null ? null : Clone(_identity));

    public Task Create(LocalDeviceIdentity identity, CancellationToken ct = default)
    {
        CreateCalls++;
        _identity = Clone(identity);
        return Task.CompletedTask;
    }

    public void Update(LocalDeviceIdentity identity)
    {
        UpdateCalls++;
        _identity = Clone(identity);
    }

    public LocalDeviceIdentity? Snapshot() =>
        _identity is null ? null : Clone(_identity);

    private static LocalDeviceIdentity Clone(LocalDeviceIdentity identity) =>
        new()
        {
            Id = identity.Id,
            OriginInstanceId = identity.OriginInstanceId,
            AgreementPrivateKeyBlob = identity.AgreementPrivateKeyBlob.ToArray(),
            SignPrivateKeyBlob = identity.SignPrivateKeyBlob.ToArray(),
            PFXCertificate = identity.PFXCertificate.ToArray(),
            DeviceType = identity.DeviceType,
            IsSyncOn = identity.IsSyncOn,
            CreatedAt = identity.CreatedAt,
            IntegrityHash = identity.IntegrityHash.ToArray()
        };
}
