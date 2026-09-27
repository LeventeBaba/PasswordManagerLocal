using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceEnrollmentRegistrationService : IDeviceEnrollmentRegistrationService
{
    private readonly IDeviceIdentityService _identity;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly ILocalNetworkAddressService _networkAddresses;
    private readonly IDeviceEnrollmentLocalLinkService _localLinks;
    private readonly IDevicePresenceRegistry? _presenceRegistry;

    public DeviceEnrollmentRegistrationService(
        IDeviceIdentityService identity,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        ILocalNetworkAddressService networkAddresses,
        IDeviceEnrollmentLocalLinkService localLinks,
        IDevicePresenceRegistry? presenceRegistry = null)
    {
        _identity = identity;
        _endpointRegistry = endpointRegistry;
        _networkAddresses = networkAddresses;
        _localLinks = localLinks;
        _presenceRegistry = presenceRegistry;
    }

    public async Task RegisterRemoteDeviceAsync(IServiceProvider services, Guid userId, EnrollmentEndpoint endpoint, CancellationToken ct)
    {
        if (await services.GetRequiredService<IDeletedUserBarrierRepository>().ExistsAsync(userId, ct))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.ProfileDataInvalid, "The account identity was permanently deleted.");

        // Endpoint cryptographic validity is deliberately not membership authority. This method only
        // rejects identity substitution; the signed DeviceAddition operation performs every mutation.
        if (endpoint.DeviceId == Guid.Empty || endpoint.OriginInstanceId == Guid.Empty ||
            endpoint.SignPublicKey.Length == 0 || endpoint.AgreementPublicKey.Length == 0 ||
            string.IsNullOrWhiteSpace(endpoint.TlsCertFingerprint) || !DeviceTypeDetector.IsValid(endpoint.DeviceType))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, "The target installation identity is incomplete.");

        var devices = services.GetRequiredService<IDeviceRepository>();
        _ = await FindExistingDeviceForEndpointAsync(devices, endpoint, ct);

        var userDevices = services.GetRequiredService<IUserDeviceRepository>();
        var existingLink = await userDevices.GetAsync(userId, endpoint.DeviceId, ct);
        if (existingLink is not null)
            existingLink.VerifyIntegrity();
    }


    private async Task<Device?> FindExistingDeviceForEndpointAsync(IDeviceRepository devices, EnrollmentEndpoint endpoint, CancellationToken ct)
    {
        var matches = new List<Device>();

        var byId = await devices.GetByIdWithUserDevicesAsync(endpoint.DeviceId, ct);
        if (byId is not null)
            matches.Add(byId);

        var byFingerprint = await devices.GetByTlsCertFingerprintWithUserDevicesAsync(endpoint.TlsCertFingerprint, ct);
        if (byFingerprint is not null)
            matches.Add(byFingerprint);

        var bySignPublicKey = await devices.GetBySignPublicKeyAsync(endpoint.SignPublicKey, ct);
        if (bySignPublicKey is not null)
            matches.Add(bySignPublicKey);

        var distinctMatches = matches
            .GroupBy(device => device.Id)
            .Select(group => group.First())
            .ToList();

        if (distinctMatches.Count == 0)
            return null;

        if (distinctMatches.Count > 1 || distinctMatches[0].Id != endpoint.DeviceId)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "A device with the same cryptographic identity already exists under a different device id.");

        var device = distinctMatches[0];
        foreach (var existingLink in device.UserDevices)
            existingLink.VerifyIntegrity();

        var identityDiffers = !device.SignPublicKey.SequenceEqual(endpoint.SignPublicKey) ||
            !device.PublicKey.SequenceEqual(endpoint.AgreementPublicKey) ||
            !string.Equals(FingerprintUtil.Normalize(device.TlsCertFingerprint), FingerprintUtil.Normalize(endpoint.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
            device.DeviceType != endpoint.DeviceType;
        if (identityDiffers && device.UserDevices.Any(link => !link.IsDeleted))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "A different active membership still uses this device identity.");

        return device;
    }


    public async Task RejectIfPrimaryUserAlreadyLinkedToLocalDeviceAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        if (await services.GetRequiredService<IDeletedUserBarrierRepository>().ExistsAsync(userId, ct))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.ProfileDataInvalid, "The account identity was permanently deleted.");

        var localUserDevices = services.GetRequiredService<ILocalUserDeviceRepository>();
        var localLink = await localUserDevices.GetAsync(userId, ct);
        if (localLink is null)
            return;

        localLink.VerifyIntegrity();
        if (localLink.LocalDeviceIdentityId == _identity.LocalDeviceId)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, "This profile is already linked to the local device.");
    }


    public async Task RegisterIncomingEnrollmentSourceEndpointAsync(
        IServiceProvider services,
        string sourceDeviceId,
        string sourceTlsCertFingerprint,
        string? sourceHost,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceHost))
            return;

        if (!Guid.TryParseExact(sourceDeviceId, "N", out var parsedDeviceId) &&
            !Guid.TryParse(sourceDeviceId, out parsedDeviceId))
            return;

        if (parsedDeviceId == Guid.Empty || parsedDeviceId == _identity.LocalDeviceId)
            return;

        if (string.IsNullOrWhiteSpace(sourceTlsCertFingerprint))
            return;

        if (IPAddress.TryParse(sourceHost, out _) &&
            _networkAddresses.GetRemoteEndpointPriority(sourceHost) == int.MinValue)
            return;

        var devices = services.GetRequiredService<IDeviceRepository>();
        var syncIdentities = services.GetRequiredService<ISyncDeviceIdentityService>();
        var syncTasks = services.GetRequiredService<IDeviceSyncTaskService>();

        var device = await devices.GetByIdAsNoTrackingAsync(parsedDeviceId, ct);
        if (device is null || !device.IsTrusted || device.IsBlocked)
            return;

        if (!string.Equals(FingerprintUtil.Normalize(device.TlsCertFingerprint), FingerprintUtil.Normalize(sourceTlsCertFingerprint), StringComparison.OrdinalIgnoreCase))
            return;

        var endpoint = new DiscoveredDeviceEndpoint
        {
            Host = sourceHost.Trim(),
            Port = SyncPort,
            TlsCertFingerprint = sourceTlsCertFingerprint
        };

        _endpointRegistry.AddOrUpdate(endpoint);
        _presenceRegistry?.RefreshAuthenticated(
            device.TlsCertFingerprint,
            endpoint,
            DevicePresenceObservationSource.Enrollment);
        syncIdentities.TryAdd(device);
        syncTasks.TryStart(endpoint, device);
    }


    public async Task QueueInitialSyncAsync(IServiceProvider services, Guid userId, Guid newDeviceId, CancellationToken ct)
    {
        if (await services.GetRequiredService<IDeletedUserBarrierRepository>().ExistsAsync(userId, ct))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.ProfileDataInvalid, "The account identity was permanently deleted.");

        var groups = services.GetRequiredService<IGroupRepository>();
        var syncQueue = services.GetRequiredService<ISyncChangeQueueService>();
        var groupIds = await groups.ListIdsByUserAsync(userId, ct);

        // These rows are written inside the enrollment transaction. Starting workers here would let
        // their separate DbContexts run before commit and incorrectly observe an empty queue.
        await syncQueue.EnqueueDeferredAsync(new SyncItem { ModelId = userId, ModelType = SyncModelType.User, ChangeType = SyncChangeType.Updated }, ct);

        foreach (var groupId in groupIds)
            await syncQueue.EnqueueDeferredAsync(new SyncItem { ModelId = groupId, ModelType = SyncModelType.Group, ChangeType = SyncChangeType.Updated }, ct);

        await syncQueue.EnqueueDeferredAsync(new SyncItem { ModelId = newDeviceId, ModelType = SyncModelType.Device, ChangeType = SyncChangeType.Created }, ct);
        await syncQueue.EnqueueDeferredAsync(new SyncItem { ModelId = SyncIdentityUtil.BuildUserDeviceModelId(userId, newDeviceId), ModelType = SyncModelType.UserDevice, ChangeType = SyncChangeType.Created }, ct);
    }
}
