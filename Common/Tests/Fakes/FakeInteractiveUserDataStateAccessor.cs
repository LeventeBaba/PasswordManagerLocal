using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeInteractiveUserDataStateAccessor : IInteractiveUserDataStateAccessor
{
    public Guid UserId { get; set; }
    public EncryptionKey? EncryptionKeyValue { get; set; }
    public UserDataBundle? UserDataBundle { get; set; }
    public int InvalidateTokenCalls { get; private set; }

    public Guid GetUserIdFromToken(Guid token) => UserId;

    public EncryptionKey GetEncryptionKeyFromToken(Guid token) =>
        EncryptionKeyValue ?? throw new InvalidOperationException("No encryption key is configured.");

    public bool TryGetUserDataBundle(Guid token, out UserDataBundle? value)
    {
        value = UserDataBundle;
        return value is not null;
    }

    public void InvalidateToken(Guid token)
    {
        InvalidateTokenCalls++;
    }
}
