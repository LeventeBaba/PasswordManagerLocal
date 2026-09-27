using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<Guid, User> _store = new();
    private readonly ConcurrentDictionary<Guid, UserLoginIdentityState> _loginIdentities = new();
    private int _listAllCallCount;
    private int _loginLookupCallCount;

    public int ListAllCallCount => Volatile.Read(ref _listAllCallCount);
    public int LoginLookupCallCount => Volatile.Read(ref _loginLookupCallCount);

    public Task AddAsync(User entity, CancellationToken ct = default)
    {
        _store[entity.UId] = Clone(entity);
        return Task.CompletedTask;
    }

    public void Delete(User entity)
    {
        _store.TryRemove(entity.UId, out _);
        _loginIdentities.TryRemove(entity.UId, out _);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.ContainsKey(id));

    public Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)_store.Keys.ToList());

    public Task<IReadOnlyList<UserLoginLookupData>> ListLoginLookupDataAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _loginLookupCallCount);

        var list = _loginIdentities.Values
            .Select(state => new UserLoginLookupData
            {
                UId = state.UserId,
                UsernameSalt = state.UsernameSalt.ToArray(),
                UsernameHash = state.UsernameHash.ToArray(),
                Status = state.Status,
                KeyEpoch = state.KeyEpoch,
                MembershipEpoch = state.MembershipEpoch
            })
            .ToList();

        return Task.FromResult((IReadOnlyList<UserLoginLookupData>)list);
    }

    public Task<IReadOnlyList<UserLoginIdentityState>> ListLoginIdentityStatesAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _loginLookupCallCount);
        return Task.FromResult((IReadOnlyList<UserLoginIdentityState>)_loginIdentities.Values.Select(Clone).ToList());
    }

    public Task<UserLoginIdentityState?> GetLoginIdentityStateAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(_loginIdentities.TryGetValue(userId, out var state) ? Clone(state) : null);

    public Task AddLoginIdentityStateAsync(UserLoginIdentityState state, CancellationToken ct = default)
    {
        _loginIdentities[state.UserId] = Clone(state);
        return Task.CompletedTask;
    }

    public void UpdateLoginIdentityState(UserLoginIdentityState state)
        => _loginIdentities[state.UserId] = Clone(state);

    public void DeleteLoginIdentityState(UserLoginIdentityState state)
        => _loginIdentities.TryRemove(state.UserId, out _);


    public Task<IReadOnlyList<User>> GetAllRememberMeEnabledUsersAsync(CancellationToken ct = default)
    {
        var list = _store.Values
            .Where(u => u.SavedKey is not null && u.SavedKey.Length > 0)
            .Select(Clone)
            .ToList();

        return Task.FromResult((IReadOnlyList<User>)list);
    }

    public Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var users = ids.Where(_store.ContainsKey).Select(id => Clone(_store[id])).ToList();
        return Task.FromResult((IReadOnlyList<User>)users);
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (_store.TryGetValue(id, out var u))
            return Task.FromResult<User?>(Clone(u));

        return Task.FromResult<User?>(null);
    }

    public Task<User?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default)
        => GetByIdAsync(id, ct);

    public Task<User?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default)
        => GetByIdAsync(id, ct);

    public Task<User?> GetByIdAsNoTrackingWithRelationsAsync(Guid id, CancellationToken ct = default)
        => GetByIdAsync(id, ct);


    public Task UpdateSavedKeyAsync(Guid id, byte[]? savedKey, CancellationToken ct = default)
    {
        if (_store.TryGetValue(id, out var user))
        {
            var replacement = Clone(user);
            replacement.SavedKey = savedKey?.ToArray();
            _store[id] = replacement;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<User>> ListAllAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _listAllCallCount);
        var list = _store.Values.Select(Clone).ToList();
        return Task.FromResult((IReadOnlyList<User>)list);
    }

    public Task<(bool found, User? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (_store.TryGetValue(id, out var u))
            return Task.FromResult((true, (User?)Clone(u)));

        return Task.FromResult((false, (User?)null));
    }

    public void Update(User entity)
    {
        _store[entity.UId] = Clone(entity);
    }

    private static User Clone(User u)
    {
        return new User
        {
            UId = u.UId,
            UsernameHash = u.UsernameHash.ToArray(),
            UsernameSalt = u.UsernameSalt.ToArray(),
            PasswordSalt = u.PasswordSalt.ToArray(),
            EncryptedPayload = u.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = u.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = u.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = u.EncryptedUserDevicesDataPayload.ToArray(),
            SavedKey = u.SavedKey is null ? null : u.SavedKey.ToArray(),
            KeyEpoch = u.KeyEpoch,
            MembershipEpoch = u.MembershipEpoch,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = u.GeneralDataVersionPhysicalTimeUnixMilliseconds,
            GeneralDataVersionLogicalCounter = u.GeneralDataVersionLogicalCounter,
            GeneralDataVersionOriginDeviceId = u.GeneralDataVersionOriginDeviceId,
            GeneralDataVersionOriginInstanceId = u.GeneralDataVersionOriginInstanceId,
            LastModifiedAt = u.LastModifiedAt,
            UserDataLastModifiedAt = u.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = u.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = u.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = u.UserDevicesDataLastModifiedAt,
            IntegrityHash = u.IntegrityHash.ToArray(),
            Groups = u.Groups.ToList(),
            LocalUserDevices = u.LocalUserDevices.ToList(),
            UserDevices = u.UserDevices.ToList()
        };
    }
    private static UserLoginIdentityState Clone(UserLoginIdentityState state)
    {
        return new UserLoginIdentityState
        {
            UserId = state.UserId,
            UsernameHash = state.UsernameHash.ToArray(),
            UsernameSalt = state.UsernameSalt.ToArray(),
            VersionPhysicalTimeUnixMilliseconds = state.VersionPhysicalTimeUnixMilliseconds,
            VersionLogicalCounter = state.VersionLogicalCounter,
            VersionOriginDeviceId = state.VersionOriginDeviceId,
            VersionOriginInstanceId = state.VersionOriginInstanceId,
            SourceOriginDeviceId = state.SourceOriginDeviceId,
            SourceOriginInstanceId = state.SourceOriginInstanceId,
            SourceOriginRevision = state.SourceOriginRevision,
            SourceSnapshotHash = state.SourceSnapshotHash.ToArray(),
            KeyEpoch = state.KeyEpoch,
            MembershipEpoch = state.MembershipEpoch,
            Status = state.Status,
            StatusReason = state.StatusReason,
            UpdatedAtUtc = state.UpdatedAtUtc,
            ConcurrencyVersion = state.ConcurrencyVersion
        };
    }

}
