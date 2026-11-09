using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Packaging;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class SignedProductReportRefresherTests
{
    [TestMethod]
    public void SignedBytesReplaceMergeHashesWithoutChangingDeduplicationMeasurements()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pml-signing-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "PasswordManagerLocal.dll");
            File.WriteAllText(path, "signed content with an appended certificate");
            var original = CreateReport();
            var report = SignedProductReportRefresher.RefreshInventory(root, original);

            Assert.AreEqual(new FileInfo(path).Length, report.Files[0].Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), report.Files[0].Sha256);
            Assert.AreEqual(report.Files[0].Length, report.Summary.FinalSizeBytes);
            Assert.AreEqual(original.Summary.SharedIdenticalBytes, report.Summary.SharedIdenticalBytes);
            Assert.AreEqual(original.Summary.BytesRemovedThroughDeduplication, report.Summary.BytesRemovedThroughDeduplication);
            Assert.AreEqual(original.Files[0].Source, report.Files[0].Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void SigningCannotSilentlyAddOrLoseProductFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pml-signing-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.ThrowsExactly<FileNotFoundException>(() => SignedProductReportRefresher.RefreshInventory(root, CreateReport()));
            File.WriteAllText(Path.Combine(root, "PasswordManagerLocal.dll"), "signed");
            File.WriteAllText(Path.Combine(root, "unexpected.dll"), "extra");
            Assert.ThrowsExactly<InvalidOperationException>(() => SignedProductReportRefresher.RefreshInventory(root, CreateReport()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RefreshingReportsPreservesTheSeparateSignatureVerificationReport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pml-signing-reports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var signatureReport = Path.Combine(root, "windows-signing-report.json");
            File.WriteAllText(signatureReport, "verified");
            PackageReportWriter.Write(CreateReport(), root, clearDirectory: false);
            Assert.AreEqual("verified", File.ReadAllText(signatureReport));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static PackageReport CreateReport() => new(
        "1.0", "win-x64", "net10.0-windows",
        new PackageSummary(1, 3, 1, 3, 1, 3, 0, 0, 0, 0, 0, 3, 1, 3),
        null,
        [new PackageFileEntry("PasswordManagerLocal.dll", PackageFileSource.Both, 3, "OLD", PackageMergeAction.DeduplicatedIdentical, "application")]);
}
