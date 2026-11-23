using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("MigrationStorage")]
[TestCategory("StoragePhysical")]
public sealed class BoundedDatabaseFileCopyTests
{
    [TestMethod]
    public async Task ExactLimit_CopiesAllBytes_WithoutChangingTheSource()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = Path.Combine(directory, "source.db");
            var destination = Path.Combine(directory, "staged.tmp");
            var bytes = Enumerable.Range(0, 64 * 1024 + 17).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            await BoundedDatabaseFileCopy.CopyAndFlushAsync(source, destination, bytes.Length, CancellationToken.None);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
            Assert.AreEqual(2, Directory.GetFiles(directory).Length);
        });
    }

    [TestMethod]
    public async Task OversizedSource_RejectsBeforeCreatingOrOverwritingTheDestination()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = Path.Combine(directory, "source.db");
            var destination = Path.Combine(directory, "backup.bak");
            var original = new byte[33];
            await File.WriteAllBytesAsync(source, original);
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
                    BoundedDatabaseFileCopy.CopyAndFlushAsync(source, destination, 32, CancellationToken.None));
                Assert.IsFalse(File.Exists(destination));
                Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            }
            await File.WriteAllTextAsync(destination, "retained backup");
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() =>
                BoundedDatabaseFileCopy.CopyAndFlushAsync(source, destination, 32, CancellationToken.None));
            Assert.AreEqual("retained backup", await File.ReadAllTextAsync(destination));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(source));
        });
    }

    [TestMethod]
    public async Task ExistingStagingSlot_IsNeverOverwrittenOrDeletedByAFailedCreate()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = Path.Combine(directory, "source.db");
            var destination = Path.Combine(directory, "fixed.tmp");
            await File.WriteAllTextAsync(source, "authoritative");
            await File.WriteAllTextAsync(destination, "interrupted candidate");
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    BoundedDatabaseFileCopy.CopyAndFlushAsync(source, destination, 128, CancellationToken.None));
                Assert.AreEqual("authoritative", await File.ReadAllTextAsync(source));
                Assert.AreEqual("interrupted candidate", await File.ReadAllTextAsync(destination));
                Assert.AreEqual(2, Directory.GetFiles(directory).Length);
            }
        });
    }

    [TestMethod]
    public async Task CancellationBeforeCopy_DoesNotAllocateAStagingFile()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = Path.Combine(directory, "source.db");
            var destination = Path.Combine(directory, "fixed.tmp");
            await File.WriteAllTextAsync(source, "authoritative");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                BoundedDatabaseFileCopy.CopyAndFlushAsync(source, destination, 128, cts.Token));
            Assert.IsFalse(File.Exists(destination));
            Assert.AreEqual("authoritative", await File.ReadAllTextAsync(source));
        });
    }

    [TestMethod]
    public async Task ChangedSourceLength_NeverCopiesPastItsAdmittedImage()
    {
        using var growing = new MemoryStream(new byte[33]);
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            BoundedDatabaseFileCopy.CopyContentAsync(growing, destination, 32, 32, CancellationToken.None));
        Assert.AreEqual(32L, destination.Length);

        using var shortened = new MemoryStream(new byte[16]);
        using var incomplete = new MemoryStream();
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() =>
            BoundedDatabaseFileCopy.CopyContentAsync(shortened, incomplete, 32, 32, CancellationToken.None));
        Assert.AreEqual(16L, incomplete.Length);
    }

    [TestMethod]
    public async Task CancellationDuringCopy_StopsBeforeFurtherChunks()
    {
        using var cts = new CancellationTokenSource();
        using var source = new MemoryStream(new byte[128 * 1024]);
        using var destination = new CancellingDestination(cts);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BoundedDatabaseFileCopy.CopyContentAsync(source, destination, source.Length, source.Length, cts.Token));
        Assert.AreEqual(64 * 1024L, destination.Length);
        Assert.AreEqual(128 * 1024L, source.Length);
    }

    private static async Task WithDirectoryAsync(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-bounded-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CancellingDestination(CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            cancellation.Cancel();
        }
    }
}
