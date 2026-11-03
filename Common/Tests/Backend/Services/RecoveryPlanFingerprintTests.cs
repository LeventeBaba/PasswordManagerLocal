using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class RecoveryPlanFingerprintTests
{
    [TestMethod]
    public void FingerprintingLeavesTheFullJournalEvidenceUnchanged()
    {
        var plan = CreatePlan();
        plan.Archive.Faults[0].RecoveryAttemptCount = 7;
        plan.Archive.Faults[0].LastRecoveryAttemptAtUtc = DateTimeOffset.UtcNow;
        plan.Archive.Snapshots[0].LastReceivedFromDeviceId = Guid.NewGuid();
        var before = JsonSerializer.SerializeToUtf8Bytes(plan.Archive, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
        SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var after = JsonSerializer.SerializeToUtf8Bytes(plan.Archive, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
        CollectionAssert.AreEqual(before, after, "Approval filtering must not erase timestamps, retry counts or receipt evidence from the journal.");
    }

    [TestMethod]
    [DataRow("profile")]
    [DataRow("operation")]
    [DataRow("snapshot")]
    [DataRow("fault-hash")]
    [DataRow("fault-blocking")]
    [DataRow("knowledge")]
    [DataRow("control")]
    [DataRow("publication")]
    [DataRow("enrollment")]
    [DataRow("risk")]
    [DataRow("blocker")]
    public void ChangedRecoveryEvidenceStillInvalidatesApproval(string change)
    {
        var plan = CreatePlan();
        var token = Guid.NewGuid();
        var device = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var before = SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, device, origin);
        switch (change)
        {
            case "profile": plan.Archive.User.EncryptedUserPasswordsDataPayload = [1]; break;
            case "operation": plan.Archive.Operations[0].EnvelopePayload = [1]; break;
            case "snapshot": plan.Archive.Snapshots[0].EnvelopePayload = [1]; break;
            case "fault-hash": plan.Archive.Faults[0].ObservedHash = [1]; break;
            case "fault-blocking": plan.Archive.Faults[0].BlocksLifecycle = true; break;
            case "knowledge": plan.Archive.Knowledge[0].HighestStoredRevision++; break;
            case "control": plan.Archive.ControlState!.NextOriginSequence++; break;
            case "publication": plan.Archive.SyncState!.NextOriginRevision++; break;
            case "enrollment": plan.Archive.EnrollmentCommits[0].Status = DeviceEnrollmentCommitStatus.TransferFailed; break;
            case "risk": plan.Risks.Add("RecoveryRiskUnmergedData"); break;
            case "blocker": plan.Blockers.Add("RecoveryBlockedDatabase"); break;
            default: Assert.Fail("Unknown change."); break;
        }
        Assert.AreNotEqual(before, SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, device, origin));
    }

    [TestMethod]
    public void ApprovalRemainsBoundToTheSessionAndExactInstallation()
    {
        var plan = CreatePlan();
        var token = Guid.NewGuid();
        var device = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var before = SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, device, origin);
        Assert.AreNotEqual(before, SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, Guid.NewGuid(), device, origin));
        Assert.AreNotEqual(before, SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, Guid.NewGuid(), origin));
        Assert.AreNotEqual(before, SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, device, Guid.NewGuid()));
    }

    private static SelfDiagnosticsRecoveryPlan CreatePlan() => new()
    {
        Archive = new UserRecoveryJournalSnapshot
        {
            Faults = [new UserSyncFault()], Operations = [new UserControlOperation()],
            Snapshots = [new UserSyncSnapshot()], Knowledge = [new UserRevisionKnowledge()],
            ControlState = new UserControlState(), SyncState = new UserSyncState(),
            EnrollmentCommits = [new DeviceEnrollmentCommit()]
        }
    };
}
