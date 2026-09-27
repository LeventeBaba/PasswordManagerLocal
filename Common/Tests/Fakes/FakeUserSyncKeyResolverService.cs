using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSyncKeyResolverService : IUserSyncKeyResolverService
{
    public bool TryResolve(User user, out EncryptionKey? key)
    {
        key = null;
        return false;
    }
}
