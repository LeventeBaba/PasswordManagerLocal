using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using System.Threading;

using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncDeviceIdentityService : ISyncDeviceIdentityService, IDisposable
{
    private readonly IDeviceIdentityService _identity;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<Guid, Device> _devicesById = new();
    private readonly Dictionary<string, Guid> _deviceIdByFingerprint = new(StringComparer.OrdinalIgnoreCase);

    public SyncDeviceIdentityService(IDeviceIdentityService identity)
    {
        _identity = identity;
    }




    public bool TryAdd(Device device)
    {
        if (!_identity.IsSyncOn)
            return false;

        if (device is null)
            return false;

        var id = device.Id;
        var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);

        _rwLock.EnterWriteLock();
        try
        {
            if (!CanStore(device, fingerprint))
            {
                RemoveUnsafe(id, fingerprint);
                return false;
            }

            AddOrUpdateUnsafe(device, fingerprint);
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }


    public int TryAdd(IReadOnlyList<Device> devices)
    {
        if (!_identity.IsSyncOn)
            return 0;

        if (devices is null || devices.Count == 0)
            return 0;

        var added = 0;

        _rwLock.EnterWriteLock();
        try
        {
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device is null)
                    continue;

                var id = device.Id;
                var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);

                if (!CanStore(device, fingerprint))
                {
                    RemoveUnsafe(id, fingerprint);
                    continue;
                }

                AddOrUpdateUnsafe(device, fingerprint);
                added++;
            }

            return added;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }


    public bool TryRemove(Device device)
    {
        if (device is null)
            return false;

        var id = device.Id;
        var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);

        _rwLock.EnterWriteLock();
        try
        {
            return RemoveUnsafe(id, fingerprint);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }


    public int Count()
    {
        _rwLock.EnterReadLock();
        try
        {
            return _devicesById.Count;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool Exists(Device device)
    {
        if (device is null)
            return false;

        var id = device.Id;
        var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);

        _rwLock.EnterReadLock();
        try
        {
            if (id != Guid.Empty && _devicesById.ContainsKey(id))
                return true;

            return fingerprint.Length != 0 && _deviceIdByFingerprint.ContainsKey(fingerprint);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool ContainsFingerprint(string fingerprint)
    {
        var normalized = FingerprintUtil.NormalizeOrEmpty(fingerprint);
        if (normalized.Length == 0)
            return false;

        _rwLock.EnterReadLock();
        try
        {
            return _deviceIdByFingerprint.ContainsKey(normalized);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool TryGetByFingerprint(string fingerprint, out Device? device)
    {
        var normalized = FingerprintUtil.NormalizeOrEmpty(fingerprint);

        _rwLock.EnterReadLock();
        try
        {
            if (normalized.Length == 0 || !_deviceIdByFingerprint.TryGetValue(normalized, out var id))
            {
                device = null;
                return false;
            }

            if (_devicesById.TryGetValue(id, out var foundDevice))
            {
                device = CloneDevice(foundDevice);
                return true;
            }

            device = null;
            return false;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool ContainsId(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
            return false;

        _rwLock.EnterReadLock();
        try
        {
            return _devicesById.ContainsKey(deviceId);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool TryGetById(Guid deviceId, out Device? device)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_devicesById.TryGetValue(deviceId, out var foundDevice))
            {
                device = CloneDevice(foundDevice);
                return true;
            }

            device = null;
            return false;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public bool IsEmpty()
    {
        _rwLock.EnterReadLock();
        try
        {
            return _devicesById.Count == 0;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }


    public void Clear()
    {
        _rwLock.EnterWriteLock();
        try
        {
            _devicesById.Clear();
            _deviceIdByFingerprint.Clear();
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }


    public void Dispose()
    {
        _rwLock.Dispose();
    }


    private bool CanStore(Device device, string fingerprint) =>
        _identity.IsSyncOn &&
        device.Id != Guid.Empty &&
        fingerprint.Length != 0 &&
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        !IsLocalDevice(device, fingerprint);


    private bool IsLocalDevice(Device device, string fingerprint)
    {
        if (!_identity.IsInitialized)
            return false;

        if (device.Id == _identity.LocalDeviceId)
            return true;

        if (fingerprint.Length != 0 && string.Equals(fingerprint, FingerprintUtil.NormalizeOrEmpty(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase))
            return true;

        return device.SignPublicKey.SequenceEqual(_identity.SignPublicKey);
    }


    private void AddOrUpdateUnsafe(Device device, string fingerprint)
    {
        if (_deviceIdByFingerprint.TryGetValue(fingerprint, out var existingIdWithFingerprint) && existingIdWithFingerprint != device.Id)
            _devicesById.Remove(existingIdWithFingerprint);

        if (_devicesById.TryGetValue(device.Id, out var existingDevice))
        {
            var oldFingerprint = FingerprintUtil.NormalizeOrEmpty(existingDevice.TlsCertFingerprint);
            if (oldFingerprint.Length != 0 && !string.Equals(oldFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                _deviceIdByFingerprint.Remove(oldFingerprint);
        }

        _devicesById[device.Id] = CloneDevice(device);
        _deviceIdByFingerprint[fingerprint] = device.Id;
    }


    private bool RemoveUnsafe(Guid id, string fingerprint)
    {
        Guid idToRemove = Guid.Empty;

        if (id != Guid.Empty && _devicesById.ContainsKey(id))
            idToRemove = id;
        else if (fingerprint.Length != 0 && _deviceIdByFingerprint.TryGetValue(fingerprint, out var mappedId))
            idToRemove = mappedId;
        else
            return false;

        if (!_devicesById.TryGetValue(idToRemove, out var existing))
            return false;

        _devicesById.Remove(idToRemove);

        var existingFingerprint = FingerprintUtil.NormalizeOrEmpty(existing.TlsCertFingerprint);
        if (existingFingerprint.Length != 0)
            _deviceIdByFingerprint.Remove(existingFingerprint);

        if (fingerprint.Length != 0)
            _deviceIdByFingerprint.Remove(fingerprint);

        return true;
    }


    private Device CloneDevice(Device source) =>
        new()
        {
            Id = source.Id,
            PublicKey = source.PublicKey.ToArray(),
            SignPublicKey = source.SignPublicKey.ToArray(),
            TlsCertFingerprint = source.TlsCertFingerprint,
            DeviceType = source.DeviceType,
            LastKnownHash = source.LastKnownHash.ToArray(),
            LastSync = UtcDateTimeUtil.ToUtc(source.LastSync),
            LastSeen = UtcDateTimeUtil.ToUtc(source.LastSeen),
            IsTrusted = source.IsTrusted,
            IsBlocked = source.IsBlocked,
            BlockedReason = source.BlockedReason,
            BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt),
            InvalidSyncAttemptCount = source.InvalidSyncAttemptCount,
            LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(source.LastInvalidSyncAttemptAt),
            LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt),
            IntegrityHash = source.IntegrityHash.ToArray()
        };


}
