using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataWriterService
{
    Task ExecuteMutationAsync(Guid token, Func<CancellationToken, Task> mutation, CancellationToken ct = default);
    Task ExecuteMutationsAsync(Guid firstToken, Guid secondToken, Func<CancellationToken, Task> mutation, CancellationToken ct = default);
    Task AddNewUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, bool enqueueSync, CancellationToken ct = default);
    Task UpdateSavedKeyOnlyAsync(User user, CancellationToken ct = default);


    Task UpdateUserDataBundleAsync(UserDataBundle bundle, User user, EncryptionKey key, UserDataBlobKind modifiedBlobs, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, User user, EncryptionKey key, UserDataBlobKind modifiedBlobs, bool enqueueSync, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, Guid token, UserDataBlobKind modifiedBlobs, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, Guid token, UserDataBlobKind modifiedBlobs, bool enqueueSync, CancellationToken ct = default);
    Task CompactUserDataBundleAsync(UserDataBundle bundle, User user, EncryptionKey key, UserDataBlobKind modifiedBlobs, CancellationToken ct = default);
    Task ReencryptUserDataBundleWithNewKeysAsync(UserDataBundle bundle, User user, EncryptionKey newUserKey, bool enqueueSync, CancellationToken ct = default);
}
