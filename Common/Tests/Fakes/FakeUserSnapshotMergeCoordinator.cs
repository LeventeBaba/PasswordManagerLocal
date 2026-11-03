using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSnapshotMergeCoordinator : IUserSnapshotMergeCoordinator
{
    public int Calls { get; private set; }
    public bool Result { get; set; }
    public Func<Guid, EncryptionKey, CancellationToken, Task<bool>>? Handler { get; set; }

    public Task<bool> TryMergePendingAsync(Guid userId, EncryptionKey key, CancellationToken ct = default)
    {
        Calls++;
        return Handler?.Invoke(userId, key, ct) ?? Task.FromResult(Result);
    }

    public Task<bool> TryMergePendingUnderLifecycleAsync(Guid userId, EncryptionKey key, CancellationToken ct = default)
    {
        Calls++;
        return Handler?.Invoke(userId, key, ct) ?? Task.FromResult(Result);
    }
}
