using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Tests.Backend.Utils;

[TestClass]
[TestCategory("StorageBudget")]
public sealed class CapacityLimitedStreamTests
{
    [TestMethod]
    public async Task ExactReadBoundary_AllowsEof_AndRejectsEvenOneTrailingByte()
    {
        using var exact = new MemoryStream(new byte[16]);
        using var reader = new CapacityLimitedStream(exact, 16);
        var buffer = new byte[64];
        Assert.AreEqual(16, await reader.ReadAsync(buffer.AsMemory()));
        Assert.AreEqual(0, await reader.ReadAsync(buffer.AsMemory()));
        using var oversized = new MemoryStream(new byte[17]);
        using var rejected = new CapacityLimitedStream(oversized, 16);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
            rejected.ReadExactlyAsync(buffer.AsMemory()).AsTask());
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
            rejected.ReadExactlyAsync(buffer.AsMemory()).AsTask());
    }

    [TestMethod]
    public async Task RepeatedPartialReads_AndSyncReads_ShareTheSameFiniteAllowance()
    {
        using var input = new MemoryStream(new byte[17]);
        using var reader = new CapacityLimitedStream(input, 16);
        var buffer = new byte[8];
        Assert.AreEqual(8, reader.Read(buffer, 0, buffer.Length));
        Assert.AreEqual(8, await reader.ReadAsync(buffer.AsMemory()));
        Assert.ThrowsExactly<StorageCapacityExceededException>(() => reader.ReadByte());
    }

    [TestMethod]
    public async Task WriteOverflow_IsRejectedBeforeReachingTheCompressedOutput()
    {
        using var output = new MemoryStream();
        using var writer = new CapacityLimitedStream(output, 16);
        writer.Write(new byte[8]);
        await writer.WriteAsync(new byte[8].AsMemory());
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(async () => await writer.WriteAsync(new byte[1].AsMemory()));
        Assert.AreEqual(16L, output.Length);
    }
}
