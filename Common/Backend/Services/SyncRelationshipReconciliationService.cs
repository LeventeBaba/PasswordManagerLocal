using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncRelationshipReconciliationService : ISyncRelationshipReconciliationService
{
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserMembershipAuthorizationRepository _membershipAuthorizations;

    public SyncRelationshipReconciliationService(
        IUserRepository users,
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationRepository membershipAuthorizations)
    {
        _users = users;
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _identity = identity;
        _membershipAuthorizations = membershipAuthorizations;
    }

    public async Task SyncUserGroupsAsync(User user, IEnumerable<Guid> groupIds, CancellationToken ct)
    {
        var ids = CreateIdSet(groupIds);

        foreach (var group in user.Groups.Where(g => !ids.Contains(g.Id)).ToList())
            user.Groups.Remove(group);

        var missingIds = ids.Where(id => user.Groups.All(g => g.Id != id)).ToArray();
        var groups = await _groups.ListByIdsAsync(missingIds, ct);
        foreach (var group in groups)
            user.Groups.Add(group);
    }


    public async Task SyncUserDevicesAsync(User user, IEnumerable<Guid> deviceIds, DateTimeOffset modifiedAt, CancellationToken ct)
    {
        var requestedIds = CreateIdSet(deviceIds);
        requestedIds.Remove(_identity.LocalDeviceId);
        var ids = new HashSet<Guid>();
        foreach (var deviceId in requestedIds)
        {
            if ((await _membershipAuthorizations.ListActiveForDeviceAsync(user.UId, deviceId, ct)).Count != 0)
                ids.Add(deviceId);
        }

        var devices = (await _devices.ListByIdsAsync(ids, ct)).ToDictionary(device => device.Id);
        var knownLinks = user.UserDevices.ToDictionary(link => link.DeviceId);
        foreach (var id in ids)
        {
            if (!devices.TryGetValue(id, out var remoteDevice))
                continue;

            if (knownLinks.TryGetValue(id, out var existingLink))
            {
                existingLink.Device = remoteDevice;
                existingLink.VerifyIntegrity();
                if (existingLink.IsDeleted)
                {
                    existingLink.IsDeleted = false;
                    existingLink.DeletedAt = null;
                    existingLink.IsSyncOn = false;
                    existingLink.LastModifiedAt = modifiedAt;
                    existingLink.GenerateIntegrityHash();
                }
                _userDevices.Update(existingLink);
                continue;
            }

            var newLink = new UserDevice
            {
                UserId = user.UId,
                DeviceId = id,
                User = user,
                Device = remoteDevice,
                IsSyncOn = false,
                IsDeleted = false,
                LastModifiedAt = modifiedAt
            };
            newLink.GenerateIntegrityHash();
            user.UserDevices.Add(newLink);
        }
    }


    public async Task SyncGroupUsersAsync(Group group, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = CreateIdSet(userIds);

        foreach (var user in group.Users.Where(u => !ids.Contains(u.UId)).ToList())
            group.Users.Remove(user);

        var missingIds = ids.Where(id => group.Users.All(u => u.UId != id)).ToArray();
        var users = await _users.ListByIdsAsync(missingIds, ct);
        foreach (var user in users)
            group.Users.Add(user);
    }


    public async Task SyncDeviceUsersAsync(Device device, IEnumerable<Guid> userIds, DateTimeOffset modifiedAt, CancellationToken ct)
    {
        var requestedIds = CreateIdSet(userIds);
        var ids = new HashSet<Guid>();
        foreach (var userId in requestedIds)
        {
            if ((await _membershipAuthorizations.ListActiveForDeviceAsync(userId, device.Id, ct)).Count != 0)
                ids.Add(userId);
        }

        var existingLinks = (await _userDevices.ListByUserIdsAndDeviceAsync(ids, device.Id, ct))
            .ToDictionary(link => link.UserId);
        var missingUserIds = ids.Where(id => !existingLinks.ContainsKey(id)).ToArray();
        var users = (await _users.ListByIdsAsync(missingUserIds, ct)).ToDictionary(user => user.UId);

        foreach (var id in ids)
        {
            if (existingLinks.TryGetValue(id, out var existingLink))
            {
                existingLink.Device = device;
                existingLink.VerifyIntegrity();
                if (existingLink.IsDeleted)
                {
                    existingLink.IsDeleted = false;
                    existingLink.DeletedAt = null;
                    existingLink.IsSyncOn = false;
                    existingLink.LastModifiedAt = modifiedAt;
                    existingLink.GenerateIntegrityHash();
                }
                _userDevices.Update(existingLink);
                continue;
            }

            if (!users.TryGetValue(id, out var user))
                continue;

            var newLink = new UserDevice
            {
                UserId = user.UId,
                DeviceId = device.Id,
                User = user,
                Device = device,
                IsSyncOn = false,
                IsDeleted = false,
                LastModifiedAt = modifiedAt
            };
            newLink.GenerateIntegrityHash();
            await _userDevices.AddAsync(newLink, ct);
        }
    }


    private HashSet<Guid> CreateIdSet(IEnumerable<Guid>? ids) =>
        ids?.Where(id => id != Guid.Empty).Distinct().ToHashSet() ?? [];
}
