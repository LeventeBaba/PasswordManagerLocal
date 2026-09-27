using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Serialization;
namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
public sealed class DataCodecTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task LargePayload_RoundTripsAcrossPooledChannelSegmentsAndAesFrames()
    {
        var payload = new CodecPayload
        {
            Name = "large streamed payload",
            Data = RandomNumberGenerator.GetBytes(512 * 1024 + 123),
            CreatedAt = DateTimeOffset.UtcNow
        };
        var associatedData = Encoding.UTF8.GetBytes("codec-associated-data");
        using var key = EncryptionKey.Create();

        var encrypted = await DataCodec.SerializeCompressEncryptAsync(
            payload,
            key,
            DataCodecTestJsonContext.Default.CodecPayload,
            associatedData: associatedData,
            aesFrameSize: 4096);
        var decoded = await DataCodec.DecryptDecompressDeserializeAsync<CodecPayload>(
            encrypted,
            key,
            DataCodecTestJsonContext.Default.CodecPayload,
            associatedData);

        MSTestAssert.IsNotNull(decoded);
        MSTestAssert.AreEqual(payload.Name, decoded.Name);
        MSTestAssert.AreEqual(payload.CreatedAt, decoded.CreatedAt);
        CollectionAssert.AreEqual(payload.Data, decoded.Data);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task WrongAssociatedData_ReturnsNull()
    {
        var payload = new CodecPayload
        {
            Name = "protected",
            Data = RandomNumberGenerator.GetBytes(32 * 1024),
            CreatedAt = DateTimeOffset.UtcNow
        };
        using var key = EncryptionKey.Create();
        var encrypted = await DataCodec.SerializeCompressEncryptAsync(
            payload,
            key,
            DataCodecTestJsonContext.Default.CodecPayload,
            associatedData: Encoding.UTF8.GetBytes("correct"),
            aesFrameSize: 1024);

        var decoded = await DataCodec.DecryptDecompressDeserializeAsync<CodecPayload>(
            encrypted,
            key,
            DataCodecTestJsonContext.Default.CodecPayload,
            Encoding.UTF8.GetBytes("wrong"));

        MSTestAssert.IsNull(decoded);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task InvalidLargePayload_DoesNotLeaveDecryptionBlockedOnFullPipe()
    {
        var invalidJson = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        invalidJson[0] = 0xFF;
        var associatedData = Encoding.UTF8.GetBytes("invalid-large-payload");
        using var key = EncryptionKey.Create();

        byte[] compressed;
        using (var source = new MemoryStream(invalidJson, writable: false))
        await using (var compressedStream = await CompressionUtil.CompressAsync(source, level: 1))
        using (var compressedOutput = new MemoryStream())
        {
            await compressedStream.CopyToAsync(compressedOutput);
            compressed = compressedOutput.ToArray();
        }

        var encrypted = await AES256.EncryptAsync(compressed, key, associatedData, frameSize: 4096);
        var decodeTask = DataCodec.DecryptDecompressDeserializeAsync<CodecPayload>(
            encrypted,
            key,
            DataCodecTestJsonContext.Default.CodecPayload,
            associatedData);

        var decoded = await decodeTask.WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.IsNull(decoded);
    }

}
