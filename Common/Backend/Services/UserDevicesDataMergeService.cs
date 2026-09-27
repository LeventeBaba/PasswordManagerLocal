using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDevicesDataMergeService : IUserDevicesDataMergeService
{
    public bool Merge(UserDevicesData local, UserDevicesData incoming)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(incoming);
        SyncVersionStampTraversal.Validate(local);
        SyncVersionStampTraversal.Validate(incoming);

        var localLive = ToUniqueDictionary(local.Devices, item => item.Id, "user-device");
        var incomingLive = ToUniqueDictionary(incoming.Devices, item => item.Id, "user-device");
        var localDeleted = ToUniqueDictionary(local.DeletedDevices, item => item.Id, "user-device-deletion");
        var incomingDeleted = ToUniqueDictionary(incoming.DeletedDevices, item => item.Id, "user-device-deletion");
        var ids = localLive.Keys
            .Concat(incomingLive.Keys)
            .Concat(localDeleted.Keys)
            .Concat(incomingDeleted.Keys)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();

        var mergedLive = new List<UserDeviceData>(ids.Length);
        var mergedDeleted = new List<DeletedUserDeviceData>();
        foreach (var id in ids)
        {
            localLive.TryGetValue(id, out var firstLive);
            incomingLive.TryGetValue(id, out var secondLive);
            localDeleted.TryGetValue(id, out var firstDeleted);
            incomingDeleted.TryGetValue(id, out var secondDeleted);

            var live = SelectSameKind(firstLive, secondLive, id, "user-device");
            var deleted = SelectSameKind(firstDeleted, secondDeleted, id, "user-device-deletion");
            if (live is null && deleted is null)
                continue;
            if (live is null)
            {
                mergedDeleted.Add(Clone(deleted!));
                continue;
            }
            if (deleted is null)
            {
                mergedLive.Add(Clone(live));
                continue;
            }

            var comparison = SyncVersionStampComparer.Instance.Compare(live.Version, deleted.Version);
            if (comparison == 0)
            {
                throw new DeterministicSyncConflictException(
                    "user-device",
                    id,
                    live.Version,
                    live.CalculateIntegrityHash(),
                    deleted.CalculateIntegrityHash());
            }

            if (comparison > 0)
                mergedLive.Add(Clone(live));
            else
                mergedDeleted.Add(Clone(deleted));
        }

        var changed =
            !Equivalent(local.Devices, mergedLive, item => item.Id) ||
            !Equivalent(local.DeletedDevices, mergedDeleted, item => item.Id);
        if (!changed)
        {
            mergedLive.ForEach(item => item.Dispose());
            mergedDeleted.ForEach(item => item.Dispose());
            return false;
        }

        local.Devices.ForEach(item => item.Dispose());
        local.DeletedDevices.ForEach(item => item.Dispose());
        local.Devices = mergedLive;
        local.DeletedDevices = mergedDeleted;
        local.GenerateIntegrityHash();
        return true;
    }

    private T? SelectSameKind<T>(T? first, T? second, Guid id, string itemType)
        where T : PasswordManagerLocal.Common.Backend.Security.IntegrityCheckableBase
    {
        if (first is null)
            return second;
        if (second is null)
            return first;

        var firstVersion = GetVersion(first);
        var secondVersion = GetVersion(second);
        var comparison = SyncVersionStampComparer.Instance.Compare(firstVersion, secondVersion);
        if (comparison != 0)
            return comparison > 0 ? first : second;

        var firstHash = first.CalculateIntegrityHash();
        var secondHash = second.CalculateIntegrityHash();
        if (!firstHash.AsSpan().SequenceEqual(secondHash))
            throw new DeterministicSyncConflictException(itemType, id, firstVersion, firstHash, secondHash);
        return first;
    }

    private SyncVersionStamp GetVersion<T>(T item) => item switch
    {
        UserDeviceData device => device.Version,
        DeletedUserDeviceData deleted => deleted.Version,
        _ => throw new InvalidOperationException($"Unsupported versioned device item type {typeof(T).Name}.")
    };

    private UserDeviceData Clone(UserDeviceData source)
    {
        var clone = new UserDeviceData
        {
            Id = source.Id,
            Name = source.Name,
            LinkedAt = source.LinkedAt,
            LastLoginDate = source.LastLoginDate,
            PreviousLoginDate = source.PreviousLoginDate,
            LastUpdatedAt = source.LastUpdatedAt,
            Version = source.Version
        };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private DeletedUserDeviceData Clone(DeletedUserDeviceData source)
    {
        var clone = new DeletedUserDeviceData
        {
            Id = source.Id,
            DeletedAt = source.DeletedAt,
            Version = source.Version,
            CausalReference = source.CausalReference
        };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private Dictionary<Guid, T> ToUniqueDictionary<T>(
        IEnumerable<T> source,
        Func<T, Guid> id,
        string itemType)
    {
        var result = new Dictionary<Guid, T>();
        foreach (var item in source)
        {
            var itemId = id(item);
            if (itemId == Guid.Empty || !result.TryAdd(itemId, item))
                throw new InvalidDataException($"Duplicate or empty {itemType} identifier {itemId:N}.");
        }
        return result;
    }

    private bool Equivalent<T>(
        IReadOnlyList<T> first,
        IReadOnlyList<T> second,
        Func<T, Guid> id)
        where T : PasswordManagerLocal.Common.Backend.Security.IntegrityCheckableBase
    {
        if (first.Count != second.Count)
            return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (id(first[index]) != id(second[index]))
                return false;
            var firstHash = first[index].CalculateIntegrityHash();
            var secondHash = second[index].CalculateIntegrityHash();
            if (!firstHash.AsSpan().SequenceEqual(secondHash))
                return false;
        }
        return true;
    }
}
