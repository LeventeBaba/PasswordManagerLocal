using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class EndpointRpcSeparationArchitectureTests
{
    [TestMethod]
    public void EndpointRpcProductionProjectsHaveOneWayDependencies()
    {
        var root = GetRepositoryRoot();
        var contracts = ProjectReferenceNames(Project(root, "Windows", "EndpointRpc", "Contracts", "PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj"));
        var client = ProjectReferenceNames(Project(root, "Windows", "EndpointRpc", "Client", "PasswordManagerLocal.Windows.EndpointRpc.Client.csproj"));
        var server = ProjectReferenceNames(Project(root, "Windows", "EndpointRpc", "Server", "PasswordManagerLocal.Windows.EndpointRpc.Server.csproj"));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "PasswordManagerLocal.Common.Contracts.csproj"
            },
            contracts);
        CollectionAssert.Contains(client, "PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj");
        CollectionAssert.Contains(client, "PasswordManagerLocal.Common.Contracts.csproj");
        CollectionAssert.Contains(client, "PasswordManagerLocal.Windows.Ipc.csproj");
        CollectionAssert.DoesNotContain(client, "PasswordManagerLocal.Common.Backend.csproj");
        CollectionAssert.DoesNotContain(client, "PasswordManagerLocal.Windows.EndpointRpc.Server.csproj");

        CollectionAssert.Contains(server, "PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj");
        CollectionAssert.Contains(server, "PasswordManagerLocal.Common.Backend.csproj");
        CollectionAssert.Contains(server, "PasswordManagerLocal.Common.Contracts.csproj");
        CollectionAssert.Contains(server, "PasswordManagerLocal.Windows.Ipc.csproj");
        CollectionAssert.DoesNotContain(server, "PasswordManagerLocal.Windows.EndpointRpc.Client.csproj");
    }

    [TestMethod]
    public void WindowsFrontendTransitiveGraphContainsNoBackendOrServerProject()
    {
        var root = GetRepositoryRoot();
        var frontend = Project(root, "Windows", "Frontend", "PasswordManagerLocal.Windows.Frontend.csproj");
        var closure = GetTransitiveProjectClosure(frontend)
            .Select(Path.GetFileName)
            .ToArray();

        CollectionAssert.Contains(closure, "PasswordManagerLocal.Common.Frontend.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Common.Contracts.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Windows.EndpointRpc.Client.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Common.Backend.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Common.Backend.Hosting.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Windows.Backend.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Windows.EndpointRpc.Server.csproj");
    }

    [TestMethod]
    public void CommonFrontendTransitiveGraphContainsNoBackendProject()
    {
        var root = GetRepositoryRoot();
        var frontend = Project(root, "Common", "Frontend", "PasswordManagerLocal.Common.Frontend.csproj");
        var closure = GetTransitiveProjectClosure(frontend)
            .Select(Path.GetFileName)
            .ToArray();

        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Common.Backend.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Common.Backend.Hosting.csproj");
    }

    [TestMethod]
    public void WindowsAgentOwnsEndpointServerAndBackendDependencies()
    {
        var root = GetRepositoryRoot();
        var agent = Project(root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj");
        var closure = GetTransitiveProjectClosure(agent)
            .Select(Path.GetFileName)
            .ToArray();

        CollectionAssert.Contains(closure, "PasswordManagerLocal.Windows.EndpointRpc.Server.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Common.Backend.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Common.Backend.Hosting.csproj");
        CollectionAssert.Contains(closure, "PasswordManagerLocal.Windows.Backend.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Windows.EndpointRpc.Client.csproj");
        CollectionAssert.DoesNotContain(closure, "PasswordManagerLocal.Common.Frontend.csproj");
    }

    [TestMethod]
    public void FrontendAndEndpointClientSourcesDoNotImportBackendNamespaces()
    {
        var root = GetRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(root, "Common", "Frontend"),
            Path.Combine(root, "Windows", "Frontend"),
            Path.Combine(root, "Windows", "EndpointRpc", "Contracts"),
            Path.Combine(root, "Windows", "EndpointRpc", "Client")
        };

        var violations = sourceRoots
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains(
                "PasswordManagerLocal.Common.Backend",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void EndpointRpcContractsSourcesDoNotReferenceClientOrServerAssemblies()
    {
        var root = GetRepositoryRoot();
        var contractsRoot = Path.Combine(root, "Windows", "EndpointRpc", "Contracts");
        var violations = Directory
            .EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains(
                           "PasswordManagerLocal.Windows.EndpointRpc.Client",
                           StringComparison.Ordinal) ||
                       source.Contains(
                           "PasswordManagerLocal.Windows.EndpointRpc.Server",
                           StringComparison.Ordinal) ||
                       source.Contains("new Client.", StringComparison.Ordinal) ||
                       source.Contains("new Server.", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void LegacyCombinedEndpointRpcProjectIsAbsent()
    {
        var root = GetRepositoryRoot();
        Assert.IsFalse(File.Exists(Project(
            root,
            "Windows",
            "EndpointRpc",
            "PasswordManagerLocal.Windows.EndpointRpc.csproj")));
    }

    [TestMethod]
    public void FrontendTrimmerRootsContainNoBackendOrEntityFrameworkAssemblies()
    {
        var project = XDocument.Load(Project(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "PasswordManagerLocal.Windows.Frontend.csproj"));
        var roots = project.Descendants("TrimmerRootAssembly")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        CollectionAssert.Contains(roots, "PasswordManagerLocal.Windows.EndpointRpc.Client");
        CollectionAssert.Contains(roots, "PasswordManagerLocal.Windows.EndpointRpc.Contracts");
        Assert.IsFalse(roots.Any(root =>
            root.Contains("Backend", StringComparison.Ordinal) ||
            root.Contains("EntityFrameworkCore", StringComparison.Ordinal) ||
            root.Contains("Sqlite", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FirewallManagerUsesSharedPortAndTargetsSiblingAgentExecutable()
    {
        var source = File.ReadAllText(Project(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "WindowsFirewallPermissionManager.cs"));

        StringAssert.Contains(source, "SyncProtocolDefaults.TcpPort");
        StringAssert.Contains(source, "WindowsExecutableNames.AgentExecutableFileName");
        Assert.IsFalse(source.Contains("AgentDeploymentDirectoryName", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Environment.ProcessPath", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("BackendDebugLog", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("SyncConstants", StringComparison.Ordinal));

        var script = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Tools",
            "ConfigureWindowsFirewall.ps1"));
        StringAssert.Contains(script, "Join-Path $PSScriptRoot 'PasswordManagerLocal.Windows.Agent.exe'");
        Assert.IsFalse(script.Contains("AgentRuntime", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PublishValidationEnforcesSharedSiblingProductBoundaries()
    {
        var root = GetRepositoryRoot();
        var publishScript = File.ReadAllText(Path.Combine(root, "Tools", "Windows", "PublishWindowsProduct.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "Tools", "Windows", "VerifyWindowsPublishedLayout.ps1"));

        StringAssert.Contains(publishScript, @"staging\frontend");
        StringAssert.Contains(publishScript, @"staging\agent");
        StringAssert.Contains(publishScript, @"product\PasswordManagerLocal");
        StringAssert.Contains(verifier, "PasswordManagerLocal.Common.Backend.dll");
        StringAssert.Contains(verifier, "PasswordManagerLocal.Windows.EndpointRpc.Server.dll");
        StringAssert.Contains(verifier, "PasswordManagerLocal.deps.json");
        StringAssert.Contains(verifier, "PasswordManagerLocal.Windows.Agent.deps.json");
        StringAssert.Contains(verifier, "prohibited legacy AgentRuntime directory");
    }

    private static string[] ProjectReferenceNames(string projectPath) =>
        ProjectReferences(projectPath)
            .Select(Path.GetFileName)
            .ToArray();

    private static HashSet<string> GetTransitiveProjectClosure(string rootProject)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootProject));

        while (pending.TryPop(out var project))
        {
            if (!closure.Add(project))
                continue;

            foreach (var reference in RuntimeProjectReferences(project))
            {
                var referencedProject = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(project)!,
                    reference));
                if (File.Exists(referencedProject))
                    pending.Push(referencedProject);
            }
        }

        return closure;
    }

    private static IEnumerable<string> RuntimeProjectReferences(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Where(element => !string.Equals(
                element.Attribute("ReferenceOutputAssembly")?.Value,
                "false",
                StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>();

    private static IEnumerable<string> ProjectReferences(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>();

    private static string Project(string root, params string[] parts) =>
        Path.Combine(new[] { root }.Concat(parts).ToArray());

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PasswordManagerLocal.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located.");
    }
}
