using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDataCachingService
{
    Task<UserDataBundle?> GetOrLoadUserDataBundleAsync(Guid token, Func<CancellationToken, Task<UserDataBundle?>> loader, CancellationToken ct = default);
    Task<UserDataBundle?> GetOrLoadUserDataBundleAsync(Guid token, Func<Task<UserDataBundle?>> loader);
    bool TryGetUserDataBundle(Guid token, out UserDataBundle? value);
    void SetUserDataBundle(Guid token, UserDataBundle value);

    Task<UserData?> GetOrLoadUserDataAsync(Guid token, Func<CancellationToken, Task<UserData?>> loader, CancellationToken ct = default);
    Task<UserData?> GetOrLoadUserDataAsync(Guid token, Func<Task<UserData?>> loader);
    bool TryGetUserData(Guid token, out UserData? value);
    void SetUserData(Guid token, UserData value);

    Task<GroupData?> GetOrLoadGroupDataAsync(Guid token, Guid groupId, Func<CancellationToken, Task<GroupData?>> loader, CancellationToken ct = default);
    Task<GroupData?> GetOrLoadGroupDataAsync(Guid token, Guid groupId, Func<Task<GroupData?>> loader);
    bool TryGetGroupData(Guid token, Guid groupId, out GroupData? value);
    void SetGroupData(Guid token, Guid groupId, GroupData value);
    void InvalidateGroup(Guid token, Guid groupId);
    void InvalidateToken(Guid token);
    void ClearAll();
}
