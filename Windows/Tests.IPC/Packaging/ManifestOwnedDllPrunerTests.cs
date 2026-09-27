using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Packaging;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class ManifestOwnedDllPrunerTests
{
    [TestMethod]
    public void RemovesOnlyDllsNotOwnedByManifestAssets()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "app.deps.json");
            File.WriteAllText(manifestPath, """
                {
                  "targets": {
                    ".NETCoreApp,Version=v10.0/win-x64": {
                      "App/1.0.0": {
                        "runtime": { "Owned.Managed.dll": {} },
                        "native": { "runtimes/win-x64/native/Owned.Native.dll": {} },
                        "runtimeTargets": { "runtimes/win-x64/lib/net10.0/Owned.Target.dll": {} }
                      }
                    }
                  }
                }
                """);

            var ownedManaged = Path.Combine(root, "Owned.Managed.dll");
            var ownedNative = Path.Combine(root, "Owned.Native.dll");
            var ownedTarget = Path.Combine(root, "Owned.Target.dll");
            var orphan = Path.Combine(root, "Orphan.Framework.dll");
            var unrelated = Path.Combine(root, "keep.txt");
            foreach (var path in new[] { ownedManaged, ownedNative, ownedTarget, orphan, unrelated })
                File.WriteAllText(path, Path.GetFileName(path));

            var reportPath = Path.Combine(root, "reports", "removed.txt");
            var removed = new ManifestOwnedDllPruner().Prune(root, [manifestPath], reportPath);

            CollectionAssert.AreEqual(new[] { "Orphan.Framework.dll" }, removed.ToArray());
            Assert.IsTrue(File.Exists(ownedManaged));
            Assert.IsTrue(File.Exists(ownedNative));
            Assert.IsTrue(File.Exists(ownedTarget));
            Assert.IsFalse(File.Exists(orphan));
            Assert.IsTrue(File.Exists(unrelated));
            CollectionAssert.AreEqual(new[] { "Orphan.Framework.dll" }, File.ReadAllLines(reportPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PasswordManagerLocal.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
