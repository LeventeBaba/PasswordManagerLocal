using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Frontend;

[TestClass]
public sealed class ManualRefreshRemovalRegressionTests
{
    [TestMethod]
    public void RetiredManualRefreshUiAndForcedPresenceEndpointRemainAbsent()
    {
        const string retiredEndpointMethod = "RefreshUserDevicePresenceAsync";
        const int retiredOperationId = 42;

        Assert.IsFalse(typeof(IEndpoints).GetMethods()
            .Any(method => method.Name == retiredEndpointMethod),
            "Manual forced-presence refresh must not return to the shared endpoint contract.");
        Assert.IsFalse(EndpointOperationManifest.All
            .Any(descriptor => descriptor.MethodName == retiredEndpointMethod),
            "Manual forced-presence refresh must not return to the EndpointRpc manifest.");
        Assert.IsFalse(Enum.IsDefined((EndpointOperationId)retiredOperationId),
            "Retired operation ID 42 remains unassigned and must not be reused.");

        var root = GetRepositoryRoot();
        var retiredUiIdentifiers = new[]
        {
            "RefreshData",
            "RefreshVisiblePageCommand",
            "RefreshVisiblePageAsync",
            retiredEndpointMethod
        };
        var productionRoots = new[]
        {
            "Common/Contracts",
            "Common/Backend",
            "Common/Frontend",
            "Android/Frontend",
            "Android/Runtime",
            "Windows/Frontend",
            "Windows/Agent",
            "Windows/EndpointRpc"
        };
        var productionFiles = productionRoots
            .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".cs" or ".axaml" or ".json")
            .ToArray();

        foreach (var file in productionFiles)
        {
            var source = File.ReadAllText(file);
            foreach (var identifier in retiredUiIdentifiers)
            {
                Assert.IsFalse(source.Contains(identifier, StringComparison.Ordinal),
                    $"Retired manual-refresh identifier '{identifier}' remains in {Path.GetRelativePath(root, file)}.");
            }
        }

        XNamespace avalonia = "https://github.com/avaloniaui";
        var viewsRoot = Path.Combine(root, "Common", "Frontend", "Views");
        foreach (var file in Directory.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            var refreshButtons = XDocument.Load(file).Descendants(avalonia + "Button")
                .Where(button => ((string?)button.Attribute("Command"))?.Contains(
                    "Refresh",
                    StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            foreach (var button in refreshButtons)
            {
                var relativePath = Path.GetRelativePath(viewsRoot, file).Replace('\\', '/');
                Assert.AreEqual("Auth/ChangeProfileView.axaml", relativePath,
                    "The remembered-profile picker may keep its list reload; authenticated app-data refresh buttons are retired.");
                Assert.AreEqual("{Binding RefreshCommand}", (string?)button.Attribute("Command"),
                    "Only the remembered-profile picker list reload should remain as a refresh button.");
            }
        }

        var frontendCodeRoots = new[]
        {
            Path.Combine(root, "Common", "Frontend"),
            Path.Combine(root, "Windows", "Frontend")
        };
        foreach (var file in frontendCodeRoots
                     .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)))
        {
            Assert.IsFalse(File.ReadAllText(file).Contains("Key.F5", StringComparison.Ordinal),
                $"The manual F5 refresh shortcut must remain absent from {Path.GetRelativePath(root, file)}.");
        }

        var localizationRoot = Path.Combine(root, "Common", "Frontend", "Assets", "Localization");
        foreach (var file in Directory.EnumerateFiles(localizationRoot, "*.json", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.IsFalse(source.Contains("Refresh data", StringComparison.OrdinalIgnoreCase),
                $"The retired authenticated refresh label remains in {Path.GetRelativePath(root, file)}.");
            Assert.IsFalse(source.Contains("Adatok frissítése", StringComparison.OrdinalIgnoreCase),
                $"The retired authenticated refresh label remains in {Path.GetRelativePath(root, file)}.");
        }
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var startPaths = new[]
        {
            Path.GetDirectoryName(sourcePath),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var startPath in startPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            for (var directory = new DirectoryInfo(startPath!); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PasswordManagerLocal.sln")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The PasswordManagerLocal solution root could not be located.");
    }
}
