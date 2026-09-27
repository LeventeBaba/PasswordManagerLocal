using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
public sealed class EncryptionKeyTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void FromRaw_CopiesCallerOwnedBuffer()
    {
        var raw = Enumerable.Repeat((byte)7, 32).ToArray();
        using var key = EncryptionKey.FromRaw(raw);

        raw[0] = 99;

        MSTestAssert.AreEqual((byte)7, key.AsSpan()[0]);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void Create_GeneratesIndependentKeys()
    {
        using var first = EncryptionKey.Create();
        using var second = EncryptionKey.Create();

        MSTestAssert.HasCount(32, first.AsSpan().ToArray());
        MSTestAssert.HasCount(32, second.AsSpan().ToArray());
        MSTestAssert.AreNotEqual(first, second);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void FromPassword_IsDeterministicAndSaltSensitive()
    {
        var password = Encoding.UTF8.GetBytes("correct horse battery staple");
        var firstSalt = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        var secondSalt = Encoding.UTF8.GetBytes("FEDCBA9876543210");

        using var first = EncryptionKey.FromPassword(password, firstSalt, 1_000, HashAlgorithmName.SHA256);
        using var same = EncryptionKey.FromPassword(password, firstSalt, 1_000, HashAlgorithmName.SHA256);
        using var different = EncryptionKey.FromPassword(password, secondSalt, 1_000, HashAlgorithmName.SHA256);

        MSTestAssert.AreEqual(first, same);
        MSTestAssert.AreEqual(first.GetHashCode(), same.GetHashCode());
        MSTestAssert.AreNotEqual(first, different);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void FromRaw_RejectsIncorrectKeySizes()
    {
        ExpectThrows<ArgumentException>(() => EncryptionKey.FromRaw(new byte[31]));
        ExpectThrows<ArgumentException>(() => EncryptionKey.FromRaw(new byte[33]));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void FromPassword_RejectsEmptyPasswordOrSalt()
    {
        ExpectThrows<ArgumentException>(() => EncryptionKey.FromPassword(Array.Empty<byte>(), new byte[] { 1 }, 1_000));
        ExpectThrows<ArgumentException>(() => EncryptionKey.FromPassword(new byte[] { 1 }, Array.Empty<byte>(), 1_000));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void Dispose_MakesKeyMaterialInaccessible()
    {
        var key = EncryptionKey.FromRaw(Enumerable.Repeat((byte)3, 32).ToArray());
        key.Dispose();

        ExpectThrows<ObjectDisposedException>(() => _ = key.AsSpan().Length);
        ExpectThrows<ObjectDisposedException>(() => key.GetHashCode());

        using var other = EncryptionKey.FromRaw(Enumerable.Repeat((byte)3, 32).ToArray());
        ExpectThrows<ObjectDisposedException>(() => _ = key == other);
    }

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
