using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class KeyVaultServiceTests
{
    private static Guid NewValidToken()
    {
        ITokenService tokens = new TokenService();
        return tokens.Issue(Guid.NewGuid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Set_TryGet_Invalidate_Works()
    {
        IKeyVaultService vault = new KeyVaultService();

        var token = NewValidToken();
        var salt = Hashing.GenerateSalt();

        using var key = EncryptionKey.FromPassword(Encoding.UTF8.GetBytes("P@ssw0rd12345678"), salt);
        vault.SetUserKey(token, key, DateTimeOffset.UtcNow.AddMinutes(5));

        MSTestAssert.IsTrue(vault.HasUserKey(token));

        MSTestAssert.IsTrue(vault.TryGetEncryptionKey(token, out var k2));
        k2.Dispose();

        vault.InvalidateToken(token);
        MSTestAssert.IsFalse(vault.HasUserKey(token));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void ExpiredKey_IsRejected()
    {
        IKeyVaultService vault = new KeyVaultService();

        var token = NewValidToken();
        var salt = Hashing.GenerateSalt();

        using var key = EncryptionKey.FromPassword(Encoding.UTF8.GetBytes("P@ssw0rd12345678"), salt);
        vault.SetUserKey(token, key, DateTimeOffset.UtcNow.AddMinutes(-1));

        MSTestAssert.IsFalse(vault.HasUserKey(token));
        MSTestAssert.IsFalse(vault.TryGetEncryptionKey(token, out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void RotateUserKey_ReplacesKeyAndPreservesEntry()
    {
        IKeyVaultService vault = new KeyVaultService();
        var token = NewValidToken();
        var firstRaw = Enumerable.Repeat((byte)1, 32).ToArray();
        var secondRaw = Enumerable.Repeat((byte)2, 32).ToArray();
        using var first = EncryptionKey.FromRaw(firstRaw);
        using var second = EncryptionKey.FromRaw(secondRaw);
        vault.SetUserKey(token, first, DateTimeOffset.UtcNow.AddMinutes(5));

        MSTestAssert.IsTrue(vault.RotateUserKey(token, second));
        MSTestAssert.IsTrue(vault.TryGetEncryptionKey(token, out var loaded));
        using (loaded)
        {
            CollectionAssert.AreEqual(secondRaw, loaded.AsSpan().ToArray());
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void PurgeExpired_RemovesOnlyExpiredEntriesAndReturnsCount()
    {
        IKeyVaultService vault = new KeyVaultService();
        var expiredToken = NewValidToken();
        var activeToken = NewValidToken();
        using var key = EncryptionKey.FromRaw(Enumerable.Repeat((byte)7, 32).ToArray());
        vault.SetUserKey(expiredToken, key, DateTimeOffset.UtcNow.AddSeconds(-1));
        vault.SetUserKey(activeToken, key, DateTimeOffset.UtcNow.AddMinutes(5));

        var removed = vault.PurgeExpired();

        MSTestAssert.AreEqual(1, removed);
        MSTestAssert.IsFalse(vault.HasUserKey(expiredToken));
        MSTestAssert.IsTrue(vault.HasUserKey(activeToken));
    }
}