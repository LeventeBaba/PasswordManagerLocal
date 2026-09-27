using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Backend.Internal.Devices;

public sealed class UserDeviceMetadataEditor
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncVersionClockService _versionClock;
    private readonly LocalUserDeviceLinkManager _localLinkManager;
    private readonly UserDeviceAccessor _accessor;

    public UserDeviceMetadataEditor(
        IUserLookupService userLookup,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        IDeviceIdentityService identity,
        ISyncVersionClockService versionClock,
        LocalUserDeviceLinkManager localLinkManager,
        UserDeviceAccessor accessor)
    {
        _userLookup = userLookup;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _identity = identity;
        _versionClock = versionClock;
        _localLinkManager = localLinkManager;
        _accessor = accessor;
    }

    public async Task SetNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct)
    {
        var normalizedName = NormalizeUserDeviceName(name);
        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        var userDevicesData = bundle.UserDevicesData;
        await _localLinkManager.GetOrCreateAsync(user.UId, ct);
        UserDevice? remoteLink = null;
        if (deviceId != _identity.LocalDeviceId)
            remoteLink = await _accessor.GetActiveRemoteAsync(user.UId, deviceId, ct);

        if (userDevicesData.Devices.Any(d => d.Id != deviceId && string.Equals(d.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidInputException();

        var encryptedDevice = userDevicesData.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (encryptedDevice is null)
        {
            var version = _versionClock.Next();
            encryptedDevice = new UserDeviceData
            {
                Id = deviceId,
                Name = normalizedName,
                LinkedAt = remoteLink?.LastModifiedAt ?? DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                Version = version
            };
            userDevicesData.DeletedDevices.RemoveAll(deleted => deleted.Id == encryptedDevice.Id);
            userDevicesData.Devices.Add(encryptedDevice);
        }
        else if (string.Equals(encryptedDevice.Name, normalizedName, StringComparison.Ordinal))
            return;
        else
        {
            encryptedDevice.Name = normalizedName;
            encryptedDevice.LastUpdatedAt = DateTimeOffset.UtcNow;
            encryptedDevice.Version = _versionClock.Next();
        }

        encryptedDevice.GenerateIntegrityHash();
        await PersistAsync(bundle, token, ct);
    }

    public bool EnsureDeviceData(UserDevicesData userDevicesData, Guid deviceId, DateTimeOffset linkedAt)
    {
        if (userDevicesData.Devices.Any(d => d.Id == deviceId))
            return false;

        var baseName = DeviceNameUtil.BuildDefaultDeviceName(deviceId);
        var version = _versionClock.Next();
        var deviceData = new UserDeviceData
        {
            Id = deviceId,
            Name = BuildUniqueEncryptedDeviceName(userDevicesData, baseName, deviceId),
            LinkedAt = linkedAt == default ? DateTimeOffset.UtcNow : linkedAt,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            Version = version
        };
        deviceData.GenerateIntegrityHash();
        userDevicesData.DeletedDevices.RemoveAll(deleted => deleted.Id == deviceData.Id);
        userDevicesData.Devices.Add(deviceData);
        return true;
    }

    private string BuildUniqueEncryptedDeviceName(UserDevicesData userDevicesData, string requestedName, Guid deviceId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? DeviceNameUtil.BuildDefaultDeviceName(deviceId) : requestedName.Trim();
        if (!IsEncryptedNameTaken(userDevicesData, baseName, deviceId)) return baseName;
        for (var i = 2; i < 100; i++)
        {
            var suffix = $"-{i}";
            var candidate = baseName[..Math.Min(baseName.Length, 64 - suffix.Length)] + suffix;
            if (!IsEncryptedNameTaken(userDevicesData, candidate, deviceId)) return candidate;
        }
        throw new InvalidInputException();
    }

    private bool IsEncryptedNameTaken(UserDevicesData userDevicesData, string name, Guid exceptDeviceId) =>
        userDevicesData.Devices.Any(d => d.Id != exceptDeviceId && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public Task PersistAsync(UserDataBundle bundle, Guid token, CancellationToken ct, bool enqueueSync = true) =>
        _userDataWriter.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, enqueueSync, ct);

    private string NormalizeUserDeviceName(string name)
    {
        if (!IsValidUserDeviceName(name)) throw new InvalidInputException();
        return name.Trim();
    }
}
