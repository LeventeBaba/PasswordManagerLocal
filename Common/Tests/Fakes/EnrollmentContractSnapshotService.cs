using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class EnrollmentContractSnapshotService : IDeviceEnrollmentSnapshotService
{
    private readonly IDeviceEnrollmentSnapshotService _inner;

    public EnrollmentContractSnapshotService(IDeviceEnrollmentSnapshotService inner)
    {
        _inner = inner;
    }

    public Task<DeviceEnrollmentSnapshot> BuildAsync(
        IServiceProvider services,
        Guid userId,
        EnrollmentEndpoint target,
        Guid authorizingAdditionOperationId,
        CancellationToken ct = default) =>
        Task.FromResult(new DeviceEnrollmentSnapshot
        {
            PrimaryUserId = userId,
            TargetDeviceId = target.DeviceId,
            TargetOriginInstanceId = target.OriginInstanceId,
            TargetTlsCertFingerprint = target.TlsCertFingerprint,
            TargetDeviceType = target.DeviceType,
            AuthorizingAdditionOperationId = authorizingAdditionOperationId
        });

    public Task EnsureEncryptedDeviceDataAsync(
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        User user,
        Guid token,
        Guid deviceId,
        SyncVersionStamp version,
        CancellationToken ct = default) =>
        _inner.EnsureEncryptedDeviceDataAsync(reader, writer, user, token, deviceId, version, ct);

    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        string sessionId,
        byte[] secret,
        byte[] plaintext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId) =>
        _inner.Encrypt(
            sessionId,
            secret,
            plaintext,
            sourceDeviceId,
            sourceOriginInstanceId,
            sourceSignPublicKey,
            sourceTlsFingerprint,
            targetDeviceId,
            targetOriginInstanceId);

    public DeviceEnrollmentSnapshot DecryptAndDeserialize(
        string sessionId,
        byte[] secret,
        byte[] ciphertext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        int encryptionVersion,
        byte[] nonce,
        byte[] tag) =>
        _inner.DecryptAndDeserialize(
            sessionId,
            secret,
            ciphertext,
            sourceDeviceId,
            sourceOriginInstanceId,
            sourceSignPublicKey,
            sourceTlsFingerprint,
            targetDeviceId,
            targetOriginInstanceId,
            encryptionVersion,
            nonce,
            tag);
}
