using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
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
    private readonly IUserDeviceRepository _userDevices;
    private readonly IUserMembershipAuthorizationRepository _membershipAuthorizations;

    public SyncRelationshipReconciliationService(
        IUserRepository users,
        IUserDeviceRepository userDevices,
        IUserMembershipAuthorizationRepository membershipAuthorizations)
    {
        _users = users;
        _userDevices = userDevices;
        _membershipAuthorizations = membershipAuthorizations;
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
