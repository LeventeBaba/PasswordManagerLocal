using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Determines whether a persisted device record represents the current physical device.
/// </summary>
public sealed class LocalDeviceMatcherService : ILocalDeviceMatcherService
{
    private readonly IDeviceIdentityService _identity;

    public LocalDeviceMatcherService(IDeviceIdentityService identity)
    {
        _identity = identity;
    }

    public bool IsLocalDevice(Device device)
    {
        if (!_identity.IsInitialized)
            return false;

        if (device.Id == _identity.LocalDeviceId)
            return true;

        if (device.SignPublicKey.SequenceEqual(_identity.SignPublicKey))
            return true;

        return string.Equals(
            FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint),
            FingerprintUtil.NormalizeOrEmpty(_identity.FingerprintHex),
            StringComparison.OrdinalIgnoreCase);
    }
}
