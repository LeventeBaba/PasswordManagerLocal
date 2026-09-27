using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSnapshotMergeCoordinator : IUserSnapshotMergeCoordinator
{
    public int Calls { get; private set; }
    public bool Result { get; set; }

    public Task<bool> TryMergePendingAsync(Guid userId, EncryptionKey key, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }

    public Task<bool> TryMergePendingUnderLifecycleAsync(Guid userId, EncryptionKey key, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
