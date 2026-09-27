using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
public sealed class AES256Tests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task DecryptAsync_ReadsLegacyMultiFrameFormat()
    {
        var keyBytes = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var plaintext = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718");
        var associatedData = Encoding.ASCII.GetBytes("aad-context");
        var legacyBlob = Convert.FromBase64String(
            "QUdDTQEBAgMEBQYHCAcAAAAHAAAAih4ss4kS88Yspfq42dTDdK/fG1rgBgsHAAAA2fR0BPQ35SwWmxsstXKjEDCcVyRWR3UHAAAAJyiVC/cQCh/JSqx1XZXW2RJdpvWMJDkEAAAApJvlxx/KVQb5qxYVgEwtKtBBCmI=");
        using var key = EncryptionKey.FromRaw(keyBytes);

        var decrypted = await AES256.DecryptAsync(legacyBlob, key, associatedData);

        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task EncryptDecrypt_MultiFrame_RoundTripsAndRejectsTampering()
    {
        var plaintext = RandomNumberGenerator.GetBytes(64 * 1024 + 37);
        var associatedData = Encoding.UTF8.GetBytes("multi-frame-aad");
        using var key = EncryptionKey.Create();

        var encrypted = await AES256.EncryptAsync(plaintext, key, associatedData, frameSize: 1024);
        var decrypted = await AES256.DecryptAsync(encrypted, key, associatedData);
        CollectionAssert.AreEqual(plaintext, decrypted);

        var tampered = encrypted.ToArray();
        tampered[32] ^= 0x80;
        await ExpectThrowsAsync<CryptographicException>(() => AES256.DecryptAsync(tampered, key, associatedData));
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
