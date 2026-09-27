using PasswordManagerLocal.Common.Backend.Models;
using System.Security.Cryptography.X509Certificates;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceIdentityService
{
    Task InitializeAsync(CancellationToken ct = default);
    bool IsInitialized { get; }
    bool IsSyncOn { get; }
    DeviceType DeviceType { get; }
    DateTimeOffset CreatedAt { get; }
    Task SetSyncOnAsync(bool isSyncOn, CancellationToken ct = default);
    byte[] AgreementPublicKey { get; }
    byte[] SignPublicKey { get; }
    Guid LocalDeviceId { get; }
    Guid OriginInstanceId { get; }
    string DeviceIdHex { get; }
    byte[] Sign(ReadOnlySpan<byte> data);
    byte[] EncryptForDevice(byte[] plaintext, byte[] recipientAgreementPublicKey, byte[] associatedData, out byte[] ephemeralPublicKey, out byte[] nonce, out byte[] tag);
    byte[] DecryptFromDevice(byte[] ciphertext, byte[] senderEphemeralPublicKey, byte[] nonce, byte[] tag, byte[] associatedData);

    X509Certificate2 Certificate { get; }
    string FingerprintHex { get; }
    string GetFingerprintHex(X509Certificate2 cert);
}