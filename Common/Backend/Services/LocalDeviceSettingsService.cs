using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class LocalDeviceSettingsService : ILocalDeviceSettingsService
{
    private readonly IUserLookupService _userLookup;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ILocalUserDeviceRepository _localUserDevices;
    private readonly ISyncChangeQueueService _syncChanges;
    private readonly IUserSyncCatchUpService _userSyncCatchUp;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly IUnitOfWork _uow;
    private readonly LocalUserDeviceLinkManager _localLinkManager;
    private readonly UserDeviceMetadataEditor _metadataEditor;

    public LocalDeviceSettingsService(
        IUserLookupService userLookup,
        IDeviceIdentityService identity,
        IUserDeviceRepository userDevices,
        ILocalUserDeviceRepository localUserDevices,
        ISyncChangeQueueService syncChanges,
        IUserSyncCatchUpService userSyncCatchUp,
        ISyncRuntimeService syncRuntime,
        IUnitOfWork uow,
        LocalUserDeviceLinkManager localLinkManager,
        UserDeviceMetadataEditor metadataEditor)
    {
        _userLookup = userLookup;
        _identity = identity;
        _userDevices = userDevices;
        _localUserDevices = localUserDevices;
        _syncChanges = syncChanges;
        _userSyncCatchUp = userSyncCatchUp;
        _syncRuntime = syncRuntime;
        _uow = uow;
        _localLinkManager = localLinkManager;
        _metadataEditor = metadataEditor;
    }

    public Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new LocalDeviceInfoResponse
        {
            DeviceId = _identity.LocalDeviceId,
            TlsCertFingerprint = _identity.FingerprintHex,
            DeviceType = _identity.DeviceType,
            IsSyncOn = _identity.IsSyncOn,
            CreatedAt = _identity.CreatedAt
        });

    public async Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default)
    {
        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        var link = await _localLinkManager.GetOrCreateAsync(user.UId, ct);
        return link.IsSyncOn;
    }

    public async Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default)
    {
        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        var link = await _localLinkManager.GetOrCreateAsync(user.UId, ct);
        if (link.IsSyncOn == isSyncOn)
            return;

        link.IsSyncOn = isSyncOn;
        link.GenerateIntegrityHash();
        _localUserDevices.Update(link);
        await _uow.SaveChangesAsync(ct);
        try
        {
            await _syncRuntime.RefreshSyncEnabledAsync(ct);

            if (isSyncOn)
            {
                var remotes = await _userDevices.ListByUserAsync(user.UId, ct);
                foreach (var deleted in remotes.Where(x => x.IsDeleted))
                {
                    await _syncChanges.EnqueueForDeviceAsync(new SyncItem
                    {
                        ModelId = SyncIdentityUtil.BuildUserDeviceModelId(deleted.UserId, deleted.DeviceId),
                        ModelType = SyncModelType.UserDevice,
                        ChangeType = SyncChangeType.Deleted,
                        ChangedAtTs = deleted.LastModifiedAt.ToUnixTimeMilliseconds()
                    }, deleted.DeviceId, ct);
                }

                foreach (var remote in remotes.Where(x => !x.IsDeleted && x.IsSyncOn))
                    await _userSyncCatchUp.EnqueueAsync(user.UId, remote.DeviceId, ct);
            }
        }
        catch (Exception ex)
        {
            throw new MutationPartiallyCommittedException(
                "The local synchronization setting was committed, but follow-up synchronization work did not complete.",
                innerException: ex);
        }
    }

    public Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default) =>
        _metadataEditor.SetNameAsync(token, _identity.LocalDeviceId, name, ct);
}
