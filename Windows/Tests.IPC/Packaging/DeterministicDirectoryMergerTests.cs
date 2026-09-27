using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Packaging;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class DeterministicDirectoryMergerTests
{
    [TestMethod]
    public void IdenticalFilesAreStoredOnceAndReportedAsDeduplicated()
    {
        using var fixture = new MergeFixture();
        fixture.WriteFrontend("shared.dll", "same");
        fixture.WriteAgent("shared.dll", "same");
        fixture.WriteFrontend("frontend.dll", "frontend");
        fixture.WriteAgent("agent.dll", "agent");

        var result = fixture.Merge();

        Assert.AreEqual(3, result.Summary.FinalFileCount);
        Assert.AreEqual(1, result.Summary.SharedIdenticalFiles);
        Assert.AreEqual(4L, result.Summary.BytesRemovedThroughDeduplication);
        Assert.AreEqual(1, result.Summary.FrontendOnlyFiles);
        Assert.AreEqual(1, result.Summary.AgentOnlyFiles);
        Assert.AreEqual("same", File.ReadAllText(Path.Combine(fixture.Output, "shared.dll")));
    }


    [TestMethod]
    public void ProcessSpecificExecutablesAndManifestsAreAllPreserved()
    {
        using var fixture = new MergeFixture();
        foreach (var name in new[]
        {
            "PasswordManagerLocal.exe",
            "PasswordManagerLocal.deps.json",
            "PasswordManagerLocal.runtimeconfig.json"
        })
        {
            fixture.WriteFrontend(name, name);
        }
        foreach (var name in new[]
        {
            "PasswordManagerLocal.Windows.Agent.exe",
            "PasswordManagerLocal.Windows.Agent.deps.json",
            "PasswordManagerLocal.Windows.Agent.runtimeconfig.json"
        })
        {
            fixture.WriteAgent(name, name);
        }
        fixture.WriteFrontend("System.Runtime.dll", "shared");
        fixture.WriteAgent("System.Runtime.dll", "shared");

        var result = fixture.Merge();

        Assert.AreEqual(7, result.Summary.FinalFileCount);
        foreach (var name in new[]
        {
            "PasswordManagerLocal.exe",
            "PasswordManagerLocal.deps.json",
            "PasswordManagerLocal.runtimeconfig.json",
            "PasswordManagerLocal.Windows.Agent.exe",
            "PasswordManagerLocal.Windows.Agent.deps.json",
            "PasswordManagerLocal.Windows.Agent.runtimeconfig.json",
            "System.Runtime.dll"
        })
        {
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Output, name)), name);
        }
        var actualBytes = Directory.EnumerateFiles(fixture.Output, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        Assert.AreEqual(result.Summary.FinalSizeBytes, actualBytes);
    }

    [TestMethod]
    public void SamePathDifferentContentFailsWithHashesSizesAndSources()
    {
        using var fixture = new MergeFixture();
        fixture.WriteFrontend("collision.dll", "frontend");
        fixture.WriteAgent("collision.dll", "agent");

        var exception = Assert.ThrowsExactly<PackagingCollisionException>(() => fixture.Merge());

        StringAssert.Contains(exception.Message, "collision.dll");
        StringAssert.Contains(exception.Message, "frontend size:");
        StringAssert.Contains(exception.Message, "frontend SHA-256:");
        StringAssert.Contains(exception.Message, "Agent size:");
        StringAssert.Contains(exception.Message, "Agent SHA-256:");
        StringAssert.Contains(exception.Message, fixture.Frontend);
        StringAssert.Contains(exception.Message, fixture.Agent);
    }

    [TestMethod]
    public void PathsThatDifferOnlyByCaseFailForWindowsSemantics()
    {
        using var fixture = new MergeFixture();
        fixture.WriteFrontend("Runtime/System.dll", "same");
        fixture.WriteAgent("runtime/system.dll", "same");

        var exception = Assert.ThrowsExactly<PackagingCollisionException>(() => fixture.Merge());
        StringAssert.Contains(exception.Message, "case-only path collision");
    }

    [TestMethod]
    public void OutputIsRecreatedSoStaleFilesCannotSurvive()
    {
        using var fixture = new MergeFixture();
        fixture.WriteFrontend("frontend.dll", "frontend");
        fixture.WriteAgent("agent.dll", "agent");
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(Path.Combine(fixture.Output, "stale.dll"), "stale");

        fixture.Merge();

        Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "stale.dll")));
    }

    [TestMethod]
    public void IdenticalInputsProduceDeterministicInventoryAndHashes()
    {
        using var fixture = new MergeFixture();
        fixture.WriteFrontend("z.dll", "z");
        fixture.WriteFrontend("a.dll", "a");
        fixture.WriteAgent("a.dll", "a");
        fixture.WriteAgent("m.dll", "m");

        var first = fixture.Merge();
        var firstProjection = first.Files.Select(ToProjection).ToArray();
        var second = fixture.Merge();
        var secondProjection = second.Files.Select(ToProjection).ToArray();

        CollectionAssert.AreEqual(firstProjection, secondProjection);
        CollectionAssert.AreEqual(new[] { "a.dll", "m.dll", "z.dll" }, second.Files.Select(file => file.RelativePath).ToArray());
    }

    private static string ToProjection(PackageFileEntry entry) =>
        $"{entry.RelativePath}|{entry.Source}|{entry.Length}|{entry.Sha256}|{entry.Action}|{entry.Classification}";

    private sealed class MergeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PmlPackageTests", Guid.NewGuid().ToString("N"));

        public MergeFixture()
        {
            Frontend = Path.Combine(_root, "frontend");
            Agent = Path.Combine(_root, "agent");
            Output = Path.Combine(_root, "output");
            Directory.CreateDirectory(Frontend);
            Directory.CreateDirectory(Agent);
        }

        public string Frontend { get; }
        public string Agent { get; }
        public string Output { get; }

        public void WriteFrontend(string relativePath, string content) => Write(Frontend, relativePath, content);
        public void WriteAgent(string relativePath, string content) => Write(Agent, relativePath, content);
        public MergeResult Merge() => new DeterministicDirectoryMerger().Merge(Frontend, Agent, Output);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
