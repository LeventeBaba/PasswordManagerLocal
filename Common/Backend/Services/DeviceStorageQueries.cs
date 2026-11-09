using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

internal static class DeviceStorageQueries
{
    public static async Task<IReadOnlyList<Device>> ListDevicesNeedingSyncAsync(
        this IDeviceRepository devices, CancellationToken ct = default) =>
        (await devices.ListTrustedUnblockedWithPendingQueueAsync(ct))
            .Where(device => device.PublicKey.Length != 0 && device.SignPublicKey.Length != 0)
            .ToList();

    public static async Task<Device?> GetBySignPublicKeyAsync(
        this IDeviceRepository devices, byte[] signPublicKey, CancellationToken ct = default)
    {
        if (signPublicKey.Length == 0)
            return null;

        var candidates = await devices.ListBySigningKeyHashAsync(Hashing.SHA256Hash(signPublicKey), ct);
        return candidates.FirstOrDefault(device => Hashing.Verify(device.SignPublicKey, signPublicKey));
    }

    public static Task<Device?> GetByTlsCertFingerprintAsync(
        this IDeviceRepository devices, string fingerprint, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? Task.FromResult<Device?>(null)
            : devices.GetByNormalizedFingerprintAsync(FingerprintUtil.Normalize(fingerprint), ct);

    public static Task<Device?> GetByTlsCertFingerprintWithUserDevicesAsync(
        this IDeviceRepository devices, string fingerprint, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? Task.FromResult<Device?>(null)
            : devices.GetByNormalizedFingerprintWithUserDevicesAsync(FingerprintUtil.Normalize(fingerprint), ct);

    public static async Task<IReadOnlyList<Device>> ListLocalSelfDevicesAsync(
        this IDeviceRepository devices, Guid localDeviceId, byte[] signPublicKey, string fingerprint,
        CancellationToken ct = default)
    {
        var normalized = FingerprintUtil.Normalize(fingerprint);
        var hash = signPublicKey.Length == 0 ? [] : Hashing.SHA256Hash(signPublicKey);
        var candidates = await devices.ListLocalIdentityCandidatesAsync(localDeviceId, hash, normalized, ct);
        return candidates.Where(device =>
            device.Id == localDeviceId ||
            (signPublicKey.Length != 0 && Hashing.Verify(device.SignPublicKey, signPublicKey)) ||
            (normalized.Length != 0 && device.TlsCertFingerprint == normalized)).ToList();
    }
}
