using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class AndroidPublishConfigurationArchitectureTests
{
    [TestMethod]
    public void AndroidPublishUsesDottedMinimumApiVersionForPlatformAnalysis()
    {
        var root = GetRepositoryRoot();
        var publishScript = File.ReadAllText(Path.Combine(root, "Tools", "Publish.ps1"));

        // The command line overrides project properties. An integer API level such as
        // "29" creates an invalid SupportedOSPlatform attribute (CA1418) and makes
        // the analyzer treat Android-only code as callable on all platforms (CA1416).
        StringAssert.Contains(
            publishScript,
            "-p:SupportedOSPlatformVersion=$($Settings.MinimumApiLevel).0");

        foreach (var project in new[] { "Backend", "Frontend" })
        {
            var projectFile = Path.Combine(
                root, "Android", project, $"PasswordManagerLocal.Android.{project}.csproj");
            var platformVersion = XDocument.Load(projectFile)
                .Descendants("SupportedOSPlatformVersion")
                .Single()
                .Value;

            Assert.AreEqual("29.0", platformVersion);
        }
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
