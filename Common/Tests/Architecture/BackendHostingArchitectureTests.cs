using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class BackendHostingArchitectureTests
{
    [TestMethod]
    public void BackendServicesAndStorageAreIndependentOfRuntimeHosting()
    {
        var backendRoot = GetBackendRoot();
        var folders = new[] { "DependencyInjection", "Services", "Repositories", "Persistence" };
        var violations = folders
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(backendRoot, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("PasswordManagerLocal.Common.Backend.Hosting", StringComparison.Ordinal) ||
                       source.Contains("PasswordManagerLocal.Common.Backend.Abstractions.Hosting", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(backendRoot, path))
            .ToArray();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static string GetBackendRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "Backend"));
}
