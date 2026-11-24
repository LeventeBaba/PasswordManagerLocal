using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class RecoveryEnrollmentSecurityArchitectureTests
{
    [TestMethod]
    [TestCategory("EnrollmentSecurity")]
    public void FaultRecoveryAndHistoryCleanup_CannotStartEnrollmentOrTransferBootstrap()
    {
        var root = RepositoryRoot();
        var services = Path.Combine(root, "Common", "Backend", "Services");
        var files = Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith("SelfDiagnostics", StringComparison.Ordinal) ||
                Path.GetFileName(path) is "UserSyncFaultService.cs" or "UserCanonicalHealthService.cs" or
                    "UserDataRecoveryCoordinator.cs" or "UserSnapshotInboxService.cs" or "UserSnapshotMergeCoordinator.cs" or
                    "UserDataRecoveryHostedService.cs" or "UserRecoveryJournalService.cs" or "UserRecoveryJournalHostedService.cs" or
                    "UserRecoverySessionService.cs" or "UserDeviceDisconnectionService.cs" or "UserForkRecoveryService.cs" or "UserLocalProfileService.cs" or "UserAccountDeletionCleanupService.cs")
            .ToArray();
        Assert.IsTrue(files.Length >= 15);
        var forbiddenCalls = new Regex(@"\b(?:StartEnrollmentAsync|StartDeviceEnrollmentAsync|AddDeviceByCodeAsync|BeginEnrollmentOnlyAsync|RegisterRemoteDeviceAsync|RegisterIncomingEnrollmentSourceEndpointAsync|AuthorizeAdditionAsync|CreateAppliedDeviceAdditionUnderLifecycleAsync|CreateGenesisAsync|TransferSnapshotAsync|ImportSnapshotAsync)\s*\(");
        var forbiddenDependencies = new Regex(@"\b(?:IDeviceEnrollmentService|IDeviceEnrollmentRegistrationService|IDeviceEnrollmentSnapshotTransferService|IDeviceEnrollmentSnapshotImporterService|IDeviceEnrollmentSnapshotService|DeviceIdentityService)\b");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.IsFalse(forbiddenCalls.IsMatch(source), Path.GetRelativePath(root, file));
            Assert.IsFalse(forbiddenDependencies.IsMatch(source), Path.GetRelativePath(root, file));
        }
    }

    [TestMethod]
    [TestCategory("EnrollmentSecurity")]
    public void DatabaseResetAndRecoveryGuidanceCannotGrantMembershipOrDeleteAccountsGlobally()
    {
        var root = RepositoryRoot();
        foreach (var relative in new[]
        {
            "Common/Backend/Persistence/BackendStorageCleaner.cs",
            "Common/Backend/Hosting/BackendRuntime.cs",
            "Common/Frontend/ViewModels/MainViewModel.cs",
            "Windows/Agent/DatabaseReset/WindowsAgentDatabaseResetCoordinator.cs"
        })
        {
            var source = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsFalse(Regex.IsMatch(source,
                @"\b(?:StartEnrollmentAsync|StartDeviceEnrollmentAsync|AddDeviceByCodeAsync|AuthorizeAdditionAsync|CreateGenesisAsync|TransferSnapshotAsync|ImportSnapshotAsync|DeleteUserAccountAsync)\s*\("), relative);
        }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PasswordManagerLocal.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source repository was not found.");
    }
}
