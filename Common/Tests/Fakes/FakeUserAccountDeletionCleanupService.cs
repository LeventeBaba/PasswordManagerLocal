using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserAccountDeletionCleanupService : IUserAccountDeletionCleanupService
{
    private readonly IUserRepository _users;

    public FakeUserAccountDeletionCleanupService(IUserRepository users) => _users = users;

    public async Task DeleteCanonicalAndPendingStateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
            return;
        user.SavedKey = null;
        user.ClearEncryptedPayloads();
        _users.Delete(user);
    }
}
