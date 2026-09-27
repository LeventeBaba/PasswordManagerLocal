using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentSnapshotService
{
    Task<DeviceEnrollmentSnapshot> BuildAsync(IServiceProvider services, Guid userId, EnrollmentEndpoint target, Guid authorizingAdditionOperationId, CancellationToken ct = default);
    Task EnsureEncryptedDeviceDataAsync(
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        User user,
        Guid token,
        Guid deviceId,
        SyncVersionStamp version,
        CancellationToken ct = default);
    (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        string sessionId,
        byte[] secret,
        byte[] plaintext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId);
    DeviceEnrollmentSnapshot DecryptAndDeserialize(
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
        byte[] tag);
}
