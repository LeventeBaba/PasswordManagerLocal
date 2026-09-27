using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;

using PasswordManagerLocal.Common.Backend.Internal.Merging;
namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserPasswordsDataMergeService : IUserPasswordsDataMergeService
{
    public bool Merge(UserPasswordsData local, UserPasswordsData incoming)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(incoming);
        SyncVersionStampTraversal.Validate(local);
        SyncVersionStampTraversal.Validate(incoming);

        var passwords = MergeVersionedCollection(
            local.Passwords,
            incoming.Passwords,
            local.DeletedPasswords,
            incoming.DeletedPasswords,
            item => item.Id,
            item => item.Version,
            deleted => deleted.Id,
            deleted => deleted.Version,
            item => item.CalculateIntegrityHash(),
            deleted => deleted.CalculateIntegrityHash(),
            Clone,
            Clone,
            "password");

        var colors = MergeVersionedCollection(
            local.CustomColors,
            incoming.CustomColors,
            local.DeletedCustomColors,
            incoming.DeletedCustomColors,
            item => item.Id,
            item => item.Version,
            deleted => deleted.Id,
            deleted => deleted.Version,
            item => item.CalculateIntegrityHash(),
            deleted => deleted.CalculateIntegrityHash(),
            Clone,
            Clone,
            "custom-color");

        var tags = MergeVersionedCollection(
            local.Tags,
            incoming.Tags,
            local.DeletedTags,
            incoming.DeletedTags,
            item => item.Id,
            item => item.Version,
            deleted => deleted.Id,
            deleted => deleted.Version,
            item => item.CalculateIntegrityHash(),
            deleted => deleted.CalculateIntegrityHash(),
            Clone,
            Clone,
            "password-tag");

        var changed =
            local.Passwords.Any(password => !password.TagIds.SequenceEqual(password.TagIds.Order())) ||
            !Equivalent(local.Passwords, passwords.Live, item => item.Id, item => item.CalculateIntegrityHash()) ||
            !Equivalent(local.DeletedPasswords, passwords.Deleted, item => item.Id, item => item.CalculateIntegrityHash()) ||
            !Equivalent(local.CustomColors, colors.Live, item => item.Id, item => item.CalculateIntegrityHash()) ||
            !Equivalent(local.DeletedCustomColors, colors.Deleted, item => item.Id, item => item.CalculateIntegrityHash()) ||
            !Equivalent(local.Tags, tags.Live, item => item.Id, item => item.CalculateIntegrityHash()) ||
            !Equivalent(local.DeletedTags, tags.Deleted, item => item.Id, item => item.CalculateIntegrityHash());

        if (!changed)
        {
            DisposeAll(passwords.Live, passwords.Deleted, colors.Live, colors.Deleted, tags.Live, tags.Deleted);
            return false;
        }

        local.Passwords.ForEach(item => item.Dispose());
        local.DeletedPasswords.ForEach(item => item.Dispose());
        local.CustomColors.ForEach(item => item.Dispose());
        local.DeletedCustomColors.ForEach(item => item.Dispose());
        local.Tags.ForEach(item => item.Dispose());
        local.DeletedTags.ForEach(item => item.Dispose());

        local.Passwords = passwords.Live;
        local.DeletedPasswords = passwords.Deleted;
        local.CustomColors = colors.Live;
        local.DeletedCustomColors = colors.Deleted;
        local.Tags = tags.Live;
        local.DeletedTags = tags.Deleted;
        local.GenerateIntegrityHash();
        return true;
    }

    private UserPasswordsMergeResult<TLive, TDeleted> MergeVersionedCollection<TLive, TDeleted>(
        IReadOnlyList<TLive> localLive,
        IReadOnlyList<TLive> incomingLive,
        IReadOnlyList<TDeleted> localDeleted,
        IReadOnlyList<TDeleted> incomingDeleted,
        Func<TLive, Guid> liveId,
        Func<TLive, SyncVersionStamp> liveVersion,
        Func<TDeleted, Guid> deletedId,
        Func<TDeleted, SyncVersionStamp> deletedVersion,
        Func<TLive, byte[]> liveHash,
        Func<TDeleted, byte[]> deletedHash,
        Func<TLive, TLive> cloneLive,
        Func<TDeleted, TDeleted> cloneDeleted,
        string itemType)
        where TLive : class
        where TDeleted : class
    {
        var localLiveById = ToUniqueDictionary(localLive, liveId, itemType, "live");
        var incomingLiveById = ToUniqueDictionary(incomingLive, liveId, itemType, "live");
        var localDeletedById = ToUniqueDictionary(localDeleted, deletedId, itemType, "deletion");
        var incomingDeletedById = ToUniqueDictionary(incomingDeleted, deletedId, itemType, "deletion");
        var ids = localLiveById.Keys
            .Concat(incomingLiveById.Keys)
            .Concat(localDeletedById.Keys)
            .Concat(incomingDeletedById.Keys)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();

        var mergedLive = new List<TLive>(ids.Length);
        var mergedDeleted = new List<TDeleted>();
        foreach (var id in ids)
        {
            localLiveById.TryGetValue(id, out var firstLive);
            incomingLiveById.TryGetValue(id, out var secondLive);
            localDeletedById.TryGetValue(id, out var firstDeleted);
            incomingDeletedById.TryGetValue(id, out var secondDeleted);

            var live = SelectSameKind(firstLive, secondLive, id, itemType, liveVersion, liveHash);
            var deleted = SelectSameKind(firstDeleted, secondDeleted, id, itemType + "-deletion", deletedVersion, deletedHash);
            if (live is null && deleted is null)
                continue;
            if (live is null)
            {
                mergedDeleted.Add(cloneDeleted(deleted!));
                continue;
            }
            if (deleted is null)
            {
                mergedLive.Add(cloneLive(live));
                continue;
            }

            var comparison = SyncVersionStampComparer.Instance.Compare(liveVersion(live), deletedVersion(deleted));
            if (comparison == 0)
            {
                throw new DeterministicSyncConflictException(
                    itemType,
                    id,
                    liveVersion(live),
                    liveHash(live),
                    deletedHash(deleted));
            }

            if (comparison > 0)
                mergedLive.Add(cloneLive(live));
            else
                mergedDeleted.Add(cloneDeleted(deleted));
        }

        return new UserPasswordsMergeResult<TLive, TDeleted>(mergedLive, mergedDeleted);
    }

    private T? SelectSameKind<T>(
        T? first,
        T? second,
        Guid itemId,
        string itemType,
        Func<T, SyncVersionStamp> version,
        Func<T, byte[]> contentHash)
        where T : class
    {
        if (first is null)
            return second;
        if (second is null)
            return first;

        var comparison = SyncVersionStampComparer.Instance.Compare(version(first), version(second));
        if (comparison != 0)
            return comparison > 0 ? first : second;

        var firstHash = contentHash(first);
        var secondHash = contentHash(second);
        if (!firstHash.AsSpan().SequenceEqual(secondHash))
            throw new DeterministicSyncConflictException(itemType, itemId, version(first), firstHash, secondHash);
        return first;
    }

    private Dictionary<Guid, T> ToUniqueDictionary<T>(
        IEnumerable<T> source,
        Func<T, Guid> idSelector,
        string itemType,
        string candidateKind)
    {
        var result = new Dictionary<Guid, T>();
        foreach (var item in source)
        {
            var id = idSelector(item);
            if (id == Guid.Empty || !result.TryAdd(id, item))
                throw new InvalidDataException($"Duplicate or empty {itemType} {candidateKind} identifier {id:N}.");
        }
        return result;
    }

    private bool Equivalent<T>(
        IReadOnlyList<T> first,
        IReadOnlyList<T> second,
        Func<T, Guid> id,
        Func<T, byte[]> hash)
    {
        if (first.Count != second.Count)
            return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (id(first[index]) != id(second[index]) ||
                !hash(first[index]).AsSpan().SequenceEqual(hash(second[index])))
                return false;
        }
        return true;
    }

    private SecurePassword Clone(SecurePassword source)
    {
        var clone = new SecurePassword
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Color = source.Color,
            Password = source.Password.ToArray(),
            TagIds = source.TagIds.Order().ToList(),
            CreatedAt = source.CreatedAt,
            LastUpdatedAt = source.LastUpdatedAt,
            Version = source.Version
        };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private DeletedPasswordData Clone(DeletedPasswordData source)
    {
        var clone = new DeletedPasswordData { Id = source.Id, DeletedAt = source.DeletedAt, Version = source.Version, CausalReference = source.CausalReference };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private CustomUserColor Clone(CustomUserColor source)
    {
        var clone = new CustomUserColor
        {
            Id = source.Id,
            ColorName = source.ColorName,
            ColorCode = source.ColorCode,
            LastUpdatedAt = source.LastUpdatedAt,
            Version = source.Version
        };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private DeletedCustomUserColorData Clone(DeletedCustomUserColorData source)
    {
        var clone = new DeletedCustomUserColorData { Id = source.Id, DeletedAt = source.DeletedAt, Version = source.Version, CausalReference = source.CausalReference };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private PasswordTag Clone(PasswordTag source)
    {
        var clone = new PasswordTag
        {
            Id = source.Id,
            Name = source.Name,
            Color = source.Color,
            LastUpdatedAt = source.LastUpdatedAt,
            Version = source.Version
        };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private DeletedPasswordTagData Clone(DeletedPasswordTagData source)
    {
        var clone = new DeletedPasswordTagData { Id = source.Id, DeletedAt = source.DeletedAt, Version = source.Version, CausalReference = source.CausalReference };
        clone.GenerateIntegrityHash();
        return clone;
    }

    private void DisposeAll(params System.Collections.IEnumerable[] collections)
    {
        foreach (var collection in collections)
            foreach (var item in collection)
                if (item is IDisposable disposable)
                    disposable.Dispose();
    }

}
