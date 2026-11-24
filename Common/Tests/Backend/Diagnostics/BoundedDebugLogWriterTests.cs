using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Diagnostics;

[TestClass]
[TestCategory("StorageAuxiliary")]
public sealed class BoundedDebugLogWriterTests
{
    [TestMethod]
    public void RepeatedWritesAndWriterRestarts_HaveFiniteFileCountAndByteLength()
    {
        WithDirectory(directory =>
        {
            for (var restart = 0; restart < 32; restart++)
            {
                var writer = CreateWriter(directory);
                for (var message = 0; message < 32; message++)
                {
                    Assert.IsTrue(writer.TryAppend($"{restart:D2}:{message:D2}:" + new string('x', 180) + "\n"));
                    var files = Directory.GetFiles(directory, "*.log");
                    Assert.IsTrue(files.Length <= 4);
                    Assert.IsTrue(files.All(path => new FileInfo(path).Length <= 1024));
                    Assert.IsTrue(files.Sum(path => new FileInfo(path).Length) <= 4096);
                }
            }
            Assert.AreEqual(0L, new FileInfo(Path.Combine(directory, "backend-debug.lock")).Length);
            Assert.IsTrue(Directory.GetFiles(directory).Length <= 5);
            Assert.IsTrue(Directory.GetFiles(directory, "*.log")
                .Any(path => File.ReadAllText(path).Contains("31:31:", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void OversizedUnicodeEntry_IsTruncatedAtAValidUtf8Boundary()
    {
        WithDirectory(directory =>
        {
            Assert.IsTrue(CreateWriter(directory).TryAppend(string.Concat(Enumerable.Repeat("😀ő", 1000))));
            var path = Directory.GetFiles(directory, "*.log").Single();
            Assert.IsTrue(new FileInfo(path).Length <= 256);
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            StringAssert.EndsWith(text, "[Log entry truncated]\n");
        });
    }

    [TestMethod]
    public void Retention_RecognizesOldLogNamesAndLeavesUnrelatedFilesUntouched()
    {
        WithDirectory(directory =>
        {
            var oversized = Path.Combine(directory, "2026-11-16_20-15-30.123_01.log");
            File.WriteAllText(oversized, new string('x', 2048));
            for (var index = 0; index < 7; index++)
            {
                var path = Path.Combine(directory, $"2026-11-16_20-15-30.12{index}.log");
                File.WriteAllText(path, "legacy");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, index, DateTimeKind.Utc));
            }
            var unrelated = Path.Combine(directory, "user-notes.log");
            File.WriteAllText(unrelated, "keep");

            Assert.IsTrue(CreateWriter(directory).TryAppend("new entry"));

            Assert.IsFalse(File.Exists(oversized));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "2026-11-16_20-15-30.120.log")));
            Assert.AreEqual("keep", File.ReadAllText(unrelated));
            Assert.AreEqual(5, Directory.GetFiles(directory, "*.log").Length);
        });
    }

    [TestMethod]
    public void LockContention_DropsDiagnosticsWithoutAllocatingAnotherFile()
    {
        WithDirectory(directory =>
        {
            var writer = CreateWriter(directory);
            using (var owner = new FileStream(Path.Combine(directory, "backend-debug.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                for (var attempt = 0; attempt < 32; attempt++)
                    Assert.IsFalse(writer.TryAppend("blocked diagnostic"));
                Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            }
            Assert.IsTrue(writer.TryAppend("after release"));
            Assert.AreEqual("after release", File.ReadAllText(Directory.GetFiles(directory, "*.log").Single()));
        });
    }

    [TestMethod]
    public async Task CompetingWriters_ShareTheSameRetentionAllowance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-debug-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var writer = CreateWriter(directory);
                var accepted = 0;
                for (var message = 0; message < 64; message++)
                    if (writer.TryAppend(new string('x', 240))) accepted++;
                return accepted;
            })));
            Assert.IsTrue(writes.Sum() > 0);
            var files = Directory.GetFiles(directory, "*.log");
            Assert.IsTrue(files.Length <= 4);
            Assert.IsTrue(files.All(path => new FileInfo(path).Length <= 1024));
            Assert.IsTrue(files.Sum(path => new FileInfo(path).Length) <= 4096);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static BoundedDebugLogWriter CreateWriter(string directory) => new(directory, 4, 1024, 256);

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-debug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
