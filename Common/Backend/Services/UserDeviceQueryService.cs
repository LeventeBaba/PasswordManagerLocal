using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDeviceQueryService : IUserDeviceQueryService
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserDeviceRepository _userDevices;
    private readonly DeviceOnlineStatusEvaluator _onlineStatusEvaluator;
    private readonly IDevicePresenceProbeService _presenceProbeService;
    private readonly LocalUserDeviceLinkManager _localLinkManager;
    private readonly UserDeviceMetadataEditor _metadataEditor;

    public UserDeviceQueryService(
        IUserLookupService userLookup,
        IUserDataReaderService userDataReader,
        IDeviceIdentityService identity,
        IUserDeviceRepository userDevices,
        DeviceOnlineStatusEvaluator onlineStatusEvaluator,
        IDevicePresenceProbeService presenceProbeService,
        LocalUserDeviceLinkManager localLinkManager,
        UserDeviceMetadataEditor metadataEditor)
    {
        _userLookup = userLookup;
        _userDataReader = userDataReader;
        _identity = identity;
        _userDevices = userDevices;
        _onlineStatusEvaluator = onlineStatusEvaluator;
        _presenceProbeService = presenceProbeService;
        _localLinkManager = localLinkManager;
        _metadataEditor = metadataEditor;
    }

    public async Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default)
    {
        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        var userDevicesData = bundle.UserDevicesData;
        var localLink = await _localLinkManager.GetOrCreateAsync(user.UId, ct);
        var links = await _userDevices.ListByUserWithDevicesAsync(user.UId, ct);

        var changed = _metadataEditor.EnsureDeviceData(userDevicesData, _identity.LocalDeviceId, DateTimeOffset.UtcNow);
        foreach (var link in links.Where(x => !x.IsDeleted))
            changed |= _metadataEditor.EnsureDeviceData(userDevicesData, link.DeviceId, link.LastModifiedAt);
        if (changed)
            await _metadataEditor.PersistAsync(bundle, token, ct);

        var encryptedDevices = userDevicesData.Devices.ToDictionary(d => d.Id);
        var visibleDeviceIds = links
            .Where(link => !link.IsDeleted)
            .Select(link => link.DeviceId)
            .Append(_identity.LocalDeviceId)
            .ToHashSet();
        var presentationNames = UserDevicePresentationUtil.ResolveNames(
            userDevicesData.Devices.Where(device => visibleDeviceIds.Contains(device.Id)));
        var result = new List<UserDeviceInfoResponse>();
        var localCanSync = localLink.IsSyncOn && _identity.IsSyncOn;
        if (localCanSync)
        {
            var probeTargets = links
                .Where(link => IsEligibleRemoteDevice(link, link.Device))
                .Select(link => link.Device!)
                .GroupBy(device => device.Id)
                .Select(group => group.First())
                .ToArray();

            await Task.WhenAll(probeTargets.Select(device =>
                _presenceProbeService.ProbeAsync(device, force: true, cancellationToken: ct)));
        }
        if (encryptedDevices.TryGetValue(_identity.LocalDeviceId, out var localDeviceData))
            result.Add(BuildLocalResponse(
                localLink,
                localDeviceData,
                presentationNames[_identity.LocalDeviceId],
                localCanSync));

        foreach (var link in links.Where(x => !x.IsDeleted && x.Device is not null))
        {
            if (encryptedDevices.TryGetValue(link.DeviceId, out var deviceData))
                result.Add(BuildRemoteResponse(
                    link,
                    link.Device!,
                    deviceData,
                    presentationNames[link.DeviceId],
                    IsRemoteDeviceOnline(localCanSync, link, link.Device!)));
        }

        return result
            .OrderByDescending(d => d.IsCurrentDevice)
            .ThenByDescending(d => d.LastSync ?? d.LastSeen ?? DateTime.MinValue)
            .ToList();
    }

    private UserDeviceInfoResponse BuildLocalResponse(
        LocalUserDevice link,
        UserDeviceData deviceData,
        string presentationName,
        bool isOnline) => new()
    {
        DeviceId = _identity.LocalDeviceId,
        Name = presentationName,
        DeviceType = _identity.DeviceType,
        TlsCertFingerprint = _identity.FingerprintHex,
        LastSync = null,
        LastSeen = null,
        LastLoginDate = ToMeaningfulUtc(deviceData.LastLoginDate),
        PreviousLoginDate = ToMeaningfulUtc(deviceData.PreviousLoginDate),
        IsTrusted = true,
        IsBlocked = false,
        InvalidSyncAttemptCount = 0,
        IsSyncOn = link.IsSyncOn,
        IsOnline = isOnline,
        IsDeleted = false,
        LinkedAt = UtcDateTimeUtil.ToUtc(deviceData.LinkedAt),
        DeletedAt = null,
        IsCurrentDevice = true
    };

    private UserDeviceInfoResponse BuildRemoteResponse(
        UserDevice link,
        Device device,
        UserDeviceData deviceData,
        string presentationName,
        bool isOnline) => new()
    {
        DeviceId = link.DeviceId,
        Name = presentationName,
        DeviceType = device.DeviceType,
        TlsCertFingerprint = device.TlsCertFingerprint,
        LastSync = ToMeaningfulUtc(device.LastSync),
        LastSeen = ToMeaningfulUtc(device.LastSeen),
        LastLoginDate = ToMeaningfulUtc(deviceData.LastLoginDate),
        PreviousLoginDate = ToMeaningfulUtc(deviceData.PreviousLoginDate),
        IsTrusted = device.IsTrusted,
        IsBlocked = device.IsBlocked,
        BlockedReason = device.BlockedReason,
        BlockedAt = UtcDateTimeUtil.ToUtc(device.BlockedAt),
        InvalidSyncAttemptCount = device.InvalidSyncAttemptCount,
        IsSyncOn = link.IsSyncOn,
        IsOnline = isOnline,
        IsDeleted = link.IsDeleted,
        LinkedAt = UtcDateTimeUtil.ToUtc(deviceData.LinkedAt),
        DeletedAt = UtcDateTimeUtil.ToUtc(link.DeletedAt),
        IsCurrentDevice = false
    };

    private DateTime? ToMeaningfulUtc(DateTime value) =>
        value == default || value == UtcDateTimeUtil.MinDateTime
            ? null
            : UtcDateTimeUtil.ToUtc(value);

    private DateTime? ToMeaningfulUtc(DateTime? value) =>
        value.HasValue ? ToMeaningfulUtc(value.Value) : null;

    private bool IsRemoteDeviceOnline(bool localCanSync, UserDevice link, Device device) =>
        localCanSync &&
        IsEligibleRemoteDevice(link, device) &&
        _onlineStatusEvaluator.IsOnline(device.TlsCertFingerprint);

    private static bool IsEligibleRemoteDevice(UserDevice link, Device? device) =>
        device is not null &&
        link.IsSyncOn &&
        !link.IsDeleted &&
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        !string.IsNullOrWhiteSpace(device.TlsCertFingerprint);
}
