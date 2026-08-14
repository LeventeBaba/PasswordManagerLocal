using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Packaging;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class StagedCollisionAnalyzerTests
{
    [TestMethod]
    public void FrameworkCollisionProducesNarrowCopyUsedAssemblyName()
    {
        using var fixture = new CollisionFixture();
        fixture.WriteFrontend("netstandard.dll", "frontend");
        fixture.WriteAgent("netstandard.dll", "agent");

        var analysis = new StagedCollisionAnalyzer().Analyze(
            fixture.Frontend,
            fixture.Agent);

        Assert.AreEqual(1, analysis.Collisions.Count);
        Assert.IsTrue(analysis.Collisions[0].CanResolveWithCopyUsed);
        Assert.AreEqual("netstandard", analysis.Collisions[0].AssemblyName);
        CollectionAssert.AreEqual(
            new[] { "netstandard" },
            analysis.CopyUsedAssemblies.ToArray());
    }

    [TestMethod]
    public void AllManagedFrameworkCollisionsAreReportedInOnePass()
    {
        using var fixture = new CollisionFixture();
        fixture.WriteFrontend("netstandard.dll", "frontend-netstandard");
        fixture.WriteAgent("netstandard.dll", "agent-netstandard");
        fixture.WriteFrontend("System.Text.Json.dll", "frontend-json");
        fixture.WriteAgent("System.Text.Json.dll", "agent-json");

        var analysis = new StagedCollisionAnalyzer().Analyze(
            fixture.Frontend,
            fixture.Agent);

        Assert.AreEqual(2, analysis.Collisions.Count);
        CollectionAssert.AreEqual(
            new[] { "System.Text.Json", "netstandard" },
            analysis.CopyUsedAssemblies.ToArray());
    }

    [TestMethod]
    public void NonManagedCollisionIsRejectedWithBothHashes()
    {
        using var fixture = new CollisionFixture();
        fixture.WriteFrontend("shared.dat", "frontend");
        fixture.WriteAgent("shared.dat", "agent");
        var reportPath = Path.Combine(fixture.Root, "reports", "collisions.json");
        var listPath = Path.Combine(fixture.Root, "reports", "assemblies.txt");

        var exception = Assert.ThrowsExactly<PackagingCollisionException>(() =>
            new StagedCollisionAnalyzer().AnalyzeAndWrite(
                fixture.Frontend,
                fixture.Agent,
                reportPath,
                listPath));

        StringAssert.Contains(exception.Message, "shared.dat");
        StringAssert.Contains(exception.Message, "frontend");
        StringAssert.Contains(exception.Message, "Agent");
        Assert.IsTrue(File.Exists(reportPath));
        Assert.IsTrue(File.Exists(listPath));
    }

    private sealed class CollisionFixture : IDisposable
    {
        public CollisionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "PmlCollisionTests", Guid.NewGuid().ToString("N"));
            Frontend = Path.Combine(Root, "frontend");
            Agent = Path.Combine(Root, "agent");
            Directory.CreateDirectory(Frontend);
            Directory.CreateDirectory(Agent);
        }

        public string Root { get; }
        public string Frontend { get; }
        public string Agent { get; }

        public void WriteFrontend(string relativePath, string content) => Write(Frontend, relativePath, content);
        public void WriteAgent(string relativePath, string content) => Write(Agent, relativePath, content);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
