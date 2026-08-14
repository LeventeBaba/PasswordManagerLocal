using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class Phase9ArchitectureGuardTests
{
    [TestMethod]
    public void ContractsRemainsPlatformIndependentAndDependencyFree()
    {
        var projectPath = Path.Combine(
            GetRepositoryRoot(),
            "Common",
            "Contracts",
            "PasswordManagerLocal.Common.Contracts.csproj");
        var project = XDocument.Load(projectPath);

        Assert.AreEqual("net10.0", Property(project, "TargetFramework"));
        Assert.IsEmpty(project.Descendants("ProjectReference").ToArray());
        Assert.IsEmpty(project.Descendants("PackageReference").ToArray());
        Assert.IsFalse(File.ReadAllText(projectPath).Contains("-windows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProductionProjectReferenceGraphHasNoCycle()
    {
        var root = GetRepositoryRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .ToArray();
        var graph = projects.ToDictionary(
            project => Path.GetFullPath(project),
            project => ProjectReferences(project)
                .Select(reference => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference)))
                .Where(File.Exists)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trail = new Stack<string>();

        foreach (var project in graph.Keys)
            Visit(project, graph, visiting, visited, trail);
    }

    [TestMethod]
    public void WindowsUiAndAgentProjectReferencesPreserveOwnershipBoundaries()
    {
        var root = GetRepositoryRoot();
        var ui = ProjectReferenceNames(Path.Combine(
            root,
            "Windows",
            "Frontend",
            "PasswordManagerLocal.Windows.Frontend.csproj"));
        var agent = ProjectReferenceNames(Path.Combine(
            root,
            "Windows",
            "Agent",
            "PasswordManagerLocal.Windows.Agent.csproj"));

        CollectionAssert.DoesNotContain(ui, "PasswordManagerLocal.Common.Backend.Hosting.csproj");
        CollectionAssert.DoesNotContain(ui, "PasswordManagerLocal.Windows.Backend.csproj");
        CollectionAssert.Contains(agent, "PasswordManagerLocal.Common.Backend.Hosting.csproj");
        CollectionAssert.Contains(agent, "PasswordManagerLocal.Windows.Backend.csproj");
        CollectionAssert.DoesNotContain(agent, "PasswordManagerLocal.Common.Frontend.csproj");
    }


    [TestMethod]
    public void AgentRemainsTheOnlyWindowsStartupRegistryWriter()
    {
        var root = GetRepositoryRoot();
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Where(path => !ContainsTestDirectory(path))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Frontend{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "new WindowsRunStartupRegistration(",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.Combine("Windows", "Agent", "Program.cs") },
            callers);
    }

    [TestMethod]
    public void ProcessTestHostIsReferencedOnlyByTheWindowsIpcTestProject()
    {
        var root = GetRepositoryRoot();
        var referencingProjects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Where(path => ProjectReferences(path).Any(reference =>
                reference.EndsWith(
                    "PasswordManagerLocal.Windows.Tests.Host.csproj",
                    StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.Combine("Windows", "Tests.IPC", "PasswordManagerLocal.Windows.Tests.IPC.csproj") },
            referencingProjects);
    }


    [TestMethod]
    public void SourceFilesUseOneMatchingPrimaryType()
    {
        var root = GetRepositoryRoot();
        var declarationPattern = new Regex(
            @"^(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+|readonly\s+)*(?:class|record(?:\s+class|\s+struct)?|struct|interface|enum)\s+(?<name>\w+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !ContainsGeneratedDirectory(path)))
        {
            var names = declarationPattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["name"].Value)
                .ToArray();
            Assert.IsTrue(
                names.Length <= 1,
                $"Multiple top-level types in {Path.GetRelativePath(root, path)}: {string.Join(", ", names)}");
            if (names.Length == 0)
                continue;

            var expectedName = Path.GetFileNameWithoutExtension(path);
            if (expectedName.EndsWith(".axaml", StringComparison.Ordinal))
                expectedName = Path.GetFileNameWithoutExtension(expectedName);
            Assert.AreEqual(
                expectedName,
                names[0],
                Path.GetRelativePath(root, path));
        }
    }

    [TestMethod]
    public void DocumentationMarkdownRemainsUnderDocsExceptForRootReadme()
    {
        var root = GetRepositoryRoot();
        var outsideDocs = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(path => !path.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(
                $"Docs{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(outsideDocs, string.Join(Environment.NewLine, outsideDocs));
    }


    [TestMethod]
    public void WindowsPublishEntryPointDelegatesToTheAuthoritativeProductPackager()
    {
        var root = GetRepositoryRoot();
        var wrapper = File.ReadAllText(Path.Combine(root, "Tools", "PublishWindowsX64.ps1"));
        var packager = File.ReadAllText(Path.Combine(root, "Tools", "Windows", "PublishWindowsProduct.ps1"));

        StringAssert.Contains(wrapper, @"Windows\PublishWindowsProduct.ps1");
        StringAssert.Contains(packager, @"Windows\Frontend\PasswordManagerLocal.Windows.Frontend.csproj");
        StringAssert.Contains(packager, @"Windows\Agent\PasswordManagerLocal.Windows.Agent.csproj");
        StringAssert.Contains(packager, @"Tools\Windows\VerifyWindowsPublishedLayout.ps1");
    }

    [TestMethod]
    public void WindowsUiProjectDefinesRepositoryRootForThePublishOptimizer()
    {
        var projectPath = Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "PasswordManagerLocal.Windows.Frontend.csproj");
        var project = XDocument.Load(projectPath);
        var repositoryRoot = project.Descendants("RepositoryRoot").Single();
        var optimizer = project.Descendants("Exec").Single(element =>
            (element.Attribute("Command")?.Value ?? string.Empty).Contains(
                "OptimizeWindowsPublish.ps1",
                StringComparison.Ordinal));

        Assert.IsFalse(string.IsNullOrWhiteSpace(repositoryRoot.Value));
        StringAssert.Contains(optimizer.Attribute("Command")!.Value, "$(RepositoryRoot)");
        StringAssert.Contains(optimizer.Attribute("Command")!.Value, "$(WindowsPublishOptimizationPath)");
        Assert.IsFalse(optimizer.Attribute("Command")!.Value.Contains(
            "-OutputPath \"$(PublishDir)\"",
            StringComparison.Ordinal));
        StringAssert.Contains(
            project.Descendants("WindowsPublishOptimizationPath").Single().Value,
            "TrimEndingDirectorySeparator");
    }

    [TestMethod]
    public void WindowsAgentPublishOptimizerAlsoNormalizesItsOutputPath()
    {
        var project = XDocument.Load(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "PasswordManagerLocal.Windows.Agent.csproj"));
        var optimizer = project.Descendants("Exec").Single(element =>
            (element.Attribute("Command")?.Value ?? string.Empty).Contains(
                "OptimizeWindowsPublish.ps1",
                StringComparison.Ordinal));

        StringAssert.Contains(optimizer.Attribute("Command")!.Value, "$(WindowsPublishOptimizationPath)");
        StringAssert.Contains(optimizer.Attribute("Command")!.Value, "-RemoveUnusedXmlSerializerAssembly");
        Assert.IsFalse(optimizer.Attribute("Command")!.Value.Contains(
            "-OutputPath \"$(PublishDir)\"",
            StringComparison.Ordinal));
        StringAssert.Contains(
            project.Descendants("WindowsPublishOptimizationPath").Single().Value,
            "TrimEndingDirectorySeparator");
    }

    [TestMethod]
    public void WindowsFrontendProjectProducesCanonicalExecutableNameDirectly()
    {
        var projectPath = Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "PasswordManagerLocal.Windows.Frontend.csproj");
        var project = XDocument.Load(projectPath);

        Assert.AreEqual(
            "PasswordManagerLocal",
            project.Descendants("AssemblyName").Single().Value);
        Assert.AreEqual(
            "PasswordManagerLocal.Windows.Frontend",
            project.Descendants("RootNamespace").Single().Value);
        Assert.IsFalse(project.Descendants("WindowsFrontendExecutableName").Any());
        Assert.IsFalse(project.Descendants("WindowsFrontendExecutableFileName").Any());
        Assert.IsFalse(project.Descendants("Target").Any(target =>
            (target.Attribute("Name")?.Value ?? string.Empty).Contains(
                "RenameWindowsFrontend",
                StringComparison.Ordinal)));

        var executableNamesSource = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "IPC",
            "Coordination",
            "WindowsExecutableNames.cs"));
        StringAssert.Contains(
            executableNamesSource,
            "UiExecutableFileName = \"PasswordManagerLocal.exe\"");
    }

    [TestMethod]
    public void WindowsPublishToolingUsesCanonicalFrontendArtifactStem()
    {
        var root = GetRepositoryRoot();
        var sources = new[]
        {
            File.ReadAllText(Path.Combine(root, "Tools", "Publish.ps1")),
            File.ReadAllText(Path.Combine(root, "Tools", "OptimizeWindowsPublish.ps1")),
            File.ReadAllText(Path.Combine(root, "Tools", "Windows", "VerifyWindowsPublishedLayout.ps1"))
        };

        Assert.IsTrue(sources.Any(source => source.Contains(
            "PasswordManagerLocal.exe",
            StringComparison.Ordinal)));
        Assert.IsTrue(sources.Any(source => source.Contains(
            "PasswordManagerLocal.dll",
            StringComparison.Ordinal)));
        Assert.IsTrue(sources.Any(source => source.Contains(
            "PasswordManagerLocal.deps.json",
            StringComparison.Ordinal)));
        Assert.IsTrue(sources.Any(source => source.Contains(
            "PasswordManagerLocal.runtimeconfig.json",
            StringComparison.Ordinal)));
        Assert.IsFalse(sources.Any(source => source.Contains(
            "PasswordManagerLocal.Windows.Frontend.dll",
            StringComparison.Ordinal)));
        Assert.IsFalse(sources.Any(source => source.Contains(
            "PasswordManagerLocal.Windows.Frontend.deps.json",
            StringComparison.Ordinal)));
        Assert.IsFalse(sources.Any(source => source.Contains(
            "PasswordManagerLocal.Windows.Frontend.runtimeconfig.json",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WindowsPublishVerifierRemainsCompatibleWithWindowsPowerShell()
    {
        var root = GetRepositoryRoot();
        var verifier = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "VerifyWindowsPublishedLayout.ps1"));

        StringAssert.Contains(verifier, ".IndexOf('\\staging\\', [StringComparison]::OrdinalIgnoreCase) -ge 0");
        StringAssert.Contains(verifier, ".IndexOf('\\reports\\', [StringComparison]::OrdinalIgnoreCase) -ge 0");
        Assert.IsFalse(verifier.Contains(".Contains('\\staging\\', [StringComparison]", StringComparison.Ordinal));
        Assert.IsFalse(verifier.Contains(".Contains('\\reports\\', [StringComparison]", StringComparison.Ordinal));
        StringAssert.Contains(verifier, "[System.IO.Path]::Combine($publish, 'PasswordManagerLocal.runtimeconfig.json')");
        StringAssert.Contains(verifier, "[System.IO.Path]::Combine($publish, 'PasswordManagerLocal.Windows.Agent.runtimeconfig.json')");
        Assert.IsFalse(verifier.Contains("Join-Path $publish 'PasswordManagerLocal.runtimeconfig.json',", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsPublishToolingNormalizesAbsolutePathsWithoutCurrentDirectoryAssumptions()
    {
        var root = GetRepositoryRoot();
        var verifier = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "VerifyWindowsPublishedLayout.ps1"));
        var packager = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "PublishWindowsProduct.ps1"));

        StringAssert.Contains(verifier, "[System.IO.Path]::GetFullPath($PublishDirectory)");
        StringAssert.Contains(packager, "[System.IO.Path]::GetFullPath($OutputRoot)");
        Assert.IsFalse(packager.Contains("Set-Location", StringComparison.Ordinal));
        Assert.IsFalse(packager.Contains("Get-Location", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsProductionSourcesContainNoTestHostOrProductionTestModeReference()
    {
        var root = GetRepositoryRoot();
        var productionSources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Where(path => !ContainsTestDirectory(path))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.Host{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.IsFalse(productionSources.Any(source =>
            source.Contains("PasswordManagerLocal.Windows.Tests.Host", StringComparison.Ordinal)));
        Assert.IsFalse(productionSources.Any(source =>
            source.Contains("PHASE9_TEST_MODE", StringComparison.Ordinal)));
    }

    private static void Visit(
        string project,
        IReadOnlyDictionary<string, string[]> graph,
        ISet<string> visiting,
        ISet<string> visited,
        Stack<string> trail)
    {
        if (visited.Contains(project))
            return;
        if (!visiting.Add(project))
        {
            var cycle = trail.Reverse().Append(project)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Fail($"Project-reference cycle: {string.Join(" -> ", cycle)}");
        }

        trail.Push(project);
        if (graph.TryGetValue(project, out var references))
        {
            foreach (var reference in references)
                Visit(reference, graph, visiting, visited, trail);
        }
        trail.Pop();
        visiting.Remove(project);
        visited.Add(project);
    }

    private static string[] ProjectReferences(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

    private static string[] ProjectReferenceNames(string projectPath) =>
        ProjectReferences(projectPath).Select(Path.GetFileName).ToArray();

    private static string? Property(XDocument project, string name) =>
        project.Descendants(name).Select(element => element.Value).FirstOrDefault();

    private static bool ContainsTestDirectory(string path) =>
        path.Contains(".Test", StringComparison.Ordinal) ||
        path.Contains(
            $"{Path.DirectorySeparatorChar}Tests.",
            StringComparison.Ordinal) ||
        path.Contains(
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    private static bool ContainsGeneratedDirectory(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
