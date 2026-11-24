using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Frontend.Services;

[TestClass]
[DoNotParallelize]
[TestCategory("StorageAuxiliary")]
public sealed class WindowsFirewallConfigurationStoreTests
{
    [TestMethod]
    public void ContendingFlagWrites_PreserveTheOwnersSlotAndThenReclaimOnlyOwnedOrphans()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-firewall-flag-{Guid.NewGuid():N}");
        try
        {
            WindowsFirewallConfigurationStore.Initialize(directory);
            var path = Path.Combine(directory, ApplicationFileNames.WindowsFirewallConfigurationFileName);
            WindowsFirewallConfigurationStore.SetConfigured(false);
            Assert.IsFalse(WindowsFirewallConfigurationStore.IsConfigured());
            var original = File.ReadAllText(path);
            var legacy = $"{path}.{Guid.NewGuid():N}.tmp";
            var unrelated = $"{path}.notes.tmp";
            File.WriteAllText(legacy, "old interrupted flag");
            File.WriteAllText(unrelated, "keep");
            using (var owner = new FileStream($"{path}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                File.WriteAllText($"{path}.tmp", "live writer slot");
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    WindowsFirewallConfigurationStore.SetConfigured(true);
                    Assert.AreEqual(original, File.ReadAllText(path));
                    Assert.AreEqual("live writer slot", File.ReadAllText($"{path}.tmp"));
                    Assert.AreEqual(5, Directory.GetFiles(directory).Length);
                }
            }

            for (var attempt = 0; attempt < 64; attempt++)
            {
                var configured = (attempt & 1) == 0;
                WindowsFirewallConfigurationStore.SetConfigured(configured);
                Assert.AreEqual(configured, WindowsFirewallConfigurationStore.IsConfigured());
                Assert.IsFalse(File.Exists($"{path}.tmp"));
                Assert.IsFalse(File.Exists(legacy));
                Assert.AreEqual("keep", File.ReadAllText(unrelated));
                Assert.AreEqual(3, Directory.GetFiles(directory).Length);
                Assert.IsTrue(new FileInfo(path).Length < 1024);
                Assert.AreEqual(0L, new FileInfo($"{path}.lock").Length);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
