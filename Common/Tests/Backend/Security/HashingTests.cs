using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
public sealed class HashingTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SHA256Hash_KnownVector_MatchesExpectedDigest()
    {
        var digest = Hashing.SHA256Hash(Encoding.ASCII.GetBytes("abc"));

        CollectionAssert.AreEqual(
            Convert.FromHexString("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"),
            digest);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SHA512Hash_KnownVector_MatchesExpectedDigest()
    {
        var digest = Hashing.SHA512Hash(Encoding.ASCII.GetBytes("abc"));

        CollectionAssert.AreEqual(
            Convert.FromHexString("DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F"),
            digest);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void HMACSHA256_KnownVector_MatchesExpectedDigest()
    {
        var digest = Hashing.HMACSHA256(
            Encoding.ASCII.GetBytes("key"),
            Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog"));

        CollectionAssert.AreEqual(
            Convert.FromHexString("F7BC83F430538424B13298E6AA6FB143EF4D59A14946175997479DBC2D1A3CD8"),
            digest);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SaltedHash_BindsDataBeforeSalt()
    {
        var data = Encoding.UTF8.GetBytes("data");
        var salt = Encoding.UTF8.GetBytes("salt");
        var combined = data.Concat(salt).ToArray();

        var expected = SHA256.HashData(combined);
        var actual = Hashing.SHA256Hash(data, salt);
        var reversed = Hashing.SHA256Hash(salt, data);

        CollectionAssert.AreEqual(expected, actual);
        MSTestAssert.IsFalse(actual.SequenceEqual(reversed));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void Verify_RejectsChangedAndDifferentLengthDigests()
    {
        var expected = Hashing.SHA256Hash(Encoding.UTF8.GetBytes("original"));
        var changed = Hashing.SHA256Hash(Encoding.UTF8.GetBytes("changed"));

        MSTestAssert.IsTrue(Hashing.Verify(expected, expected.ToArray()));
        MSTestAssert.IsFalse(Hashing.Verify(expected, changed));
        MSTestAssert.IsFalse(Hashing.Verify(expected, expected[..^1]));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void GenerateSalt_UsesRequestedLengthAndRejectsInvalidLength()
    {
        var first = Hashing.GenerateSalt(48);
        var second = Hashing.GenerateSalt(48);

        MSTestAssert.HasCount(48, first);
        MSTestAssert.HasCount(48, second);
        MSTestAssert.IsFalse(first.SequenceEqual(second));
        ExpectThrows<ArgumentOutOfRangeException>(() => Hashing.GenerateSalt(0));
        ExpectThrows<ArgumentOutOfRangeException>(() => Hashing.GenerateSalt(-1));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void WriteString_PreservesLengthPrefixedUtf8HashFormat()
    {
        string?[] values =
        [
            null,
            string.Empty,
            "árvíztűrő tükörfúrógép 🚀",
            new string('x', 1024) + "終"
        ];

        foreach (var value in values)
        {
            var utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var legacyMaterial = new byte[sizeof(int) + utf8.Length];
            BinaryPrimitives.WriteInt32LittleEndian(legacyMaterial.AsSpan(0, sizeof(int)), utf8.Length);
            utf8.CopyTo(legacyMaterial, sizeof(int));

            var expected = SHA256.HashData(legacyMaterial);
            var actual = Hashing.SHA256Hash(hash => hash.WriteString(value));

            CollectionAssert.AreEqual(expected, actual, $"Hash format changed for value length {value?.Length ?? 0}.");
        }
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
