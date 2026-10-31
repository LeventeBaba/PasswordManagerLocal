using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class WindowsPublicationArchitectureTests
{
    [TestMethod]
    public void ReleaseWinX64ProjectsDeclareCompatibleTrimSettings()
    {
        var root = GetRepositoryRoot();
        foreach (var relativePath in new[]
        {
            Path.Combine("Windows", "Frontend", "PasswordManagerLocal.Windows.Frontend.csproj"),
            Path.Combine("Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj")
        })
        {
            var project = XDocument.Load(Path.Combine(root, relativePath));
            var releaseProperties = project.Root!.Elements("PropertyGroup").Single(group =>
                (string?)group.Attribute("Condition") == "'$(Configuration)' == 'Release' and '$(RuntimeIdentifier)' == 'win-x64'");
            AssertProperty(releaseProperties, "SelfContained", "true");
            AssertProperty(releaseProperties, "PublishTrimmed", "true");
            AssertProperty(releaseProperties, "TrimMode", "partial");
            AssertProperty(releaseProperties, "PublishSingleFile", "false");
            AssertProperty(releaseProperties, "PublishReadyToRun", "false");
            AssertProperty(releaseProperties, "InvariantGlobalization", "false");
            AssertProperty(releaseProperties, "JsonSerializerIsReflectionEnabledByDefault", "false");
        }
    }

    [TestMethod]
    public void AgentHasNoBroadTrimmerRootOrGlobalWarningSuppression()
    {
        var project = XDocument.Load(Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));

        Assert.IsFalse(project.Descendants("TrimmerRootAssembly").Any());
        Assert.IsFalse(project.Descendants("TrimmerRootDescriptor").Any());
        Assert.IsFalse(project.Descendants("NoWarn").Any(element =>
            element.Value.Contains("IL2", StringComparison.OrdinalIgnoreCase) ||
            element.Value.Contains("ILLink", StringComparison.OrdinalIgnoreCase)));
        AssertProperty(project, "SuppressTrimAnalysisWarnings", "false");
    }

    [TestMethod]
    public void WindowsPublishToolingStagesSeparateProjectsAndValidatesSharedProductLayout()
    {
        var root = GetRepositoryRoot();
        var packager = File.ReadAllText(Path.Combine(root, "Tools", "Windows", "PublishWindowsProduct.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "Tools", "Windows", "VerifyWindowsPublishedLayout.ps1"));

        foreach (var required in new[]
        {
            "Remove-Item -LiteralPath $outputRootPath -Recurse -Force", @"staging\frontend", @"staging\agent",
            @"product\PasswordManagerLocal", "reports", "-p:PublishTrimmed=true", "-p:PublishSingleFile=false",
            "-p:PublishReadyToRun=false", "--frontend", "--agent", "--output", "--report"
        })
            StringAssert.Contains(packager, required);
        foreach (var required in new[]
        {
            "PasswordManagerLocal.Common.Backend.dll", "PasswordManagerLocal.Windows.EndpointRpc.Server.dll",
            "PasswordManagerLocal.deps.json", "PasswordManagerLocal.Windows.Agent.deps.json"
        })
            StringAssert.Contains(verifier, required);
    }

    [TestMethod]
    public void WindowsArchiveUsesZipSafeDeterministicTimestampsAndSelfValidation()
    {
        var root = GetRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "PublishWindowsProduct.ps1"));

        StringAssert.Contains(script, "function New-DeterministicProductArchive");
        StringAssert.Contains(script, "[System.IO.Compression.ZipArchive]::new(");
        StringAssert.Contains(script, "[DateTimeOffset]::new(2000, 1, 1");
        StringAssert.Contains(script, "$entry.LastWriteTime = $fixedZipTimestamp");
        StringAssert.Contains(script, "[System.IO.Compression.ZipArchiveMode]::Read");
        StringAssert.Contains(script, "Product archive entry count mismatch");
        StringAssert.Contains(script, "Product archive entry mismatch at index");
        StringAssert.Contains(script, "Verified deterministic ZIP archive");
        StringAssert.Contains(script, "New-DeterministicProductArchive -SourceDirectory $productRoot -DestinationPath $zipPath");
        Assert.IsFalse(script.Contains("Compress-Archive", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentOptimizerRemovesCopyPromotedXmlSerializerFacadeAndManifestAsset()
    {
        var root = GetRepositoryRoot();
        var agentProject = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));
        var optimizer = File.ReadAllText(Path.Combine(root, "Tools", "OptimizeWindowsPublish.ps1"));
        var validator = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "WindowsProductValidator.cs"));

        StringAssert.Contains(agentProject, "-RemoveUnusedXmlSerializerAssembly");
        StringAssert.Contains(optimizer, "[switch]$RemoveUnusedXmlSerializerAssembly");
        StringAssert.Contains(optimizer, "System.Xml.XmlSerializer.dll");
        StringAssert.Contains(optimizer, "runtimeTargets");
        StringAssert.Contains(optimizer, "System.Xml.XmlSerializer/*");
        StringAssert.Contains(optimizer, "EndsWith('.pdb'");
        StringAssert.Contains(optimizer, "resources");
        StringAssert.Contains(validator, "System.Xml.XmlSerializer.dll");
    }

    [TestMethod]
    public void PublicationPrunesAndRejectsDllsNotOwnedByDependencyManifests()
    {
        var root = GetRepositoryRoot();
        var packager = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "PublishWindowsProduct.ps1"));
        var validator = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "WindowsProductValidator.cs"));
        var pruner = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "ManifestOwnedDllPruner.cs"));
        var inventory = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "DependencyManifestAssetInventory.cs"));
        var program = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "ProgramEntry.cs"));

        StringAssert.Contains(packager, "Remove-StagedUnownedDlls");
        StringAssert.Contains(packager, "prune-unowned-dlls");
        StringAssert.Contains(packager, "PasswordManagerLocal.deps.json");
        StringAssert.Contains(packager, "PasswordManagerLocal.Windows.Agent.deps.json");
        StringAssert.Contains(program, "RunPruneUnownedDlls");
        StringAssert.Contains(pruner, "Directory.EnumerateFiles(root, \"*.dll\"");
        StringAssert.Contains(pruner, "File.Delete(file.AbsolutePath)");
        StringAssert.Contains(inventory, "runtimeTargets");
        StringAssert.Contains(inventory, "native");
        StringAssert.Contains(validator, "AssertAllDllsOwnedByManifests");
        StringAssert.Contains(validator, "final product");
        StringAssert.Contains(validator, "not owned by its dependency manifest(s)");
    }

    [TestMethod]
    public void SharedRuntimeUsesCollisionDrivenDirectLinkerActions()
    {
        var root = GetRepositoryRoot();
        var targets = File.ReadAllText(Path.Combine(
            root, "Windows", "Packaging", "SharedRuntimeTrimming.targets"));
        StringAssert.Contains(targets, "AfterTargets=\"PrepareForILLink\"");
        StringAssert.Contains(targets, "BeforeTargets=\"_RunILLink\"");
        StringAssert.Contains(targets, "SharedCopyUsedAssemblyListFile");
        StringAssert.Contains(targets, "SharedCopyAssemblyListFile");
        StringAssert.Contains(targets, "ReadLinesFromFile");
        StringAssert.Contains(targets, "--action copyused %(Identity)");
        StringAssert.Contains(targets, "--action copy %(Identity)");
        StringAssert.Contains(targets, "_ExtraTrimmerArgs");
        Assert.IsTrue(
            targets.IndexOf("--action copyused %(Identity)", StringComparison.Ordinal) <
            targets.IndexOf("--action copy %(Identity)", StringComparison.Ordinal));
        Assert.IsFalse(targets.Contains("ResolvedFileToPublish", StringComparison.Ordinal));
        Assert.IsFalse(targets.Contains("<ManagedAssemblyToLink Remove=", StringComparison.Ordinal));
        Assert.IsFalse(targets.Contains("<ManagedAssemblyToLink Include=", StringComparison.Ordinal));
        Assert.IsFalse(targets.Contains("_PrepareTrimConfiguration", StringComparison.Ordinal));

        var script = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "PublishWindowsProduct.ps1"));
        StringAssert.Contains(script, "analyze-collisions");
        StringAssert.Contains(script, "staged-collision-analysis.json");
        StringAssert.Contains(script, "staged-copyused-assemblies.txt");
        StringAssert.Contains(script, "staged-copy-assemblies.txt");
        StringAssert.Contains(script, "-p:SharedCopyUsedAssemblyListFile=");
        StringAssert.Contains(script, "-p:SharedCopyAssemblyListFile=");
        Assert.IsFalse(script.Contains("-p:SharedCopyUsedAssemblies=", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("$copyUsedAssemblies -join \",\"", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("-p:IntermediateLinkDir=", StringComparison.Ordinal));
        StringAssert.Contains(script, "Reset-ProjectLinkerIntermediate");
        StringAssert.Contains(script, "obj\\$Configuration\\net10.0-windows\\$RuntimeIdentifier\\linked");
        StringAssert.Contains(script, "Remove-Item -LiteralPath $linkDirectory -Recurse -Force");
        StringAssert.Contains(script, "Assert-StagedFiles");
        StringAssert.Contains(script, "after the $passName frontend publish");
        StringAssert.Contains(script, "after the $passName Agent publish");
        StringAssert.Contains(script, "Republishing both isolated stages with narrow ILLink copyused actions");
        StringAssert.Contains(script, "Residual collisions remain after copyused; republishing with ILLink copy");
        StringAssert.Contains(script, "Same-path managed collisions remain even after ILLink copy harmonization");
        StringAssert.Contains(script, "Write-AssemblyListFile");

        foreach (var projectPath in new[]
        {
            Path.Combine(root, "Windows", "Frontend", "PasswordManagerLocal.Windows.Frontend.csproj"),
            Path.Combine(root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj")
        })
        {
            var projectText = File.ReadAllText(projectPath);
            StringAssert.Contains(projectText, "SharedRuntimeTrimming.targets");
            Assert.IsFalse(projectText.Contains("PackagingIntermediateLinkRoot", StringComparison.Ordinal));
            Assert.IsFalse(projectText.Contains("<IntermediateLinkDir", StringComparison.Ordinal));
        }
    }

    private static void AssertProperty(XContainer project, string name, string expected)
    {
        var values = project.Descendants(name).Select(element => element.Value.Trim()).ToArray();
        Assert.IsTrue(values.Contains(expected, StringComparer.OrdinalIgnoreCase),
            $"Expected <{name}>{expected}</{name}>.");
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
