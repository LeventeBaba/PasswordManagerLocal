using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class ConfirmedDeviceHistoryRecoveryTests
{
    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ActiveLinkWithoutActiveMembership_IsDetectedAndRemovedAfterConfirmation()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("orphan_device_link"));
        await EnrollAsync(source, removed, token);
        await source.Endpoints.DisconnectUserDeviceAsync(token, removed.Identity.LocalDeviceId, Password());
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<IUserDeviceRepository>();
            var link = (await links.GetAsync(before.UId, removed.Identity.LocalDeviceId))!;
            link.IsDeleted = false;
            link.DeletedAt = null;
            link.IsSyncOn = true;
            links.Update(link);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(assessment.Healthy);
        CollectionAssert.Contains(assessment.Findings.ToArray(), "DeviceLinkInvalid");
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskOtherDevices");
        Assert.IsNotNull(assessment.RecoveryPlanId);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count, "An active orphan link requires review before resetting history.");
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        Assert.IsTrue((await source.Endpoints.GetUserDevicesAsync(token)).All(device => device.IsCurrentDevice));
        using var verify = source.Services.CreateScope();
        var stored = (await verify.ServiceProvider.GetRequiredService<IUserDeviceRepository>()
            .GetAsync(before.UId, removed.Identity.LocalDeviceId))!;
        Assert.IsTrue(stored.IsDeleted);
        Assert.IsFalse(stored.IsSyncOn);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ConfirmedReset_RefreshesRuntimeAfterCommitAndLifecycleRelease(bool failRefresh)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var before = await ReadUserAsync(source, token);
        using var scope = source.Services.CreateScope();
        var lifecycle = new LifecycleProbe(scope.ServiceProvider.GetRequiredService<IUserLifecycleCoordinator>());
        var runtime = new FakeSyncRuntimeService
        {
            RefreshHandler = async _ =>
            {
                Assert.IsFalse(lifecycle.IsExecuting, "Runtime shutdown must not wait for workers while holding their profile lock.");
                using var read = source.Services.CreateScope();
                var local = await read.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>().GetAsync(before.UId);
                var user = await read.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(before.UId);
                Assert.IsFalse(local!.IsSyncOn);
                Assert.IsTrue(user!.KeyEpoch > before.KeyEpoch,
                    "A separate scope must see the committed recovery before runtime reconciliation.");
                if (failRefresh) throw new IOException("Injected runtime refresh failure.");
            }
        };
        var diagnostics = ActivatorUtilities.CreateInstance<SelfDiagnosticsService>(scope.ServiceProvider, lifecycle, runtime);
        var assessment = await diagnostics.RunAsync(token);
        Assert.AreEqual(0, runtime.RefreshSyncEnabledCalls);
        if (failRefresh)
            await Assert.ThrowsExactlyAsync<MutationPartiallyCommittedException>(() =>
                diagnostics.ConfirmRepairAsync(token, assessment.RecoveryPlanId!, Password()));
        else
            Assert.IsTrue((await diagnostics.ConfirmRepairAsync(token, assessment.RecoveryPlanId!, Password())).Healthy);
        Assert.AreEqual(1, runtime.RefreshSyncEnabledCalls);
        Assert.IsTrue((await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token)).Healthy,
            "A runtime failure must not undo committed history or require another reset.");
    }

    [TestMethod]
    [Timeout(120_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ConfirmedRecovery_PreservesSecretsOtherProfileAndUnmergedEvidence_ThenAllowsPairing()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        await using var next = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var other = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("unaffected_recovery"));
        await source.Endpoints.AddNewPasswordAsync(other, new NewPasswordRequest { Name = "Other secret", Password = Secret() });
        var before = await ReadUserAsync(source, token);
        Assert.IsTrue(before.SavedKey is null or { Length: 0 },
            "This scenario must start without a remembered key.");
        var otherBefore = await ReadUserAsync(source, other);
        byte[] retainedEnvelope;
        using (var scope = source.Services.CreateScope())
        {
            var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
            var snapshot = (await snapshots.ListForUserAsync(before.UId)).First();
            retainedEnvelope = snapshot.EnvelopePayload.ToArray();
            snapshot.Status = UserSyncSnapshotStatus.IsolatedFork;
            snapshot.QuarantineReason = "test retained fork";
            snapshot.ConflictingSnapshotHash = new byte[32];
            snapshots.Update(snapshot);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        Assert.IsTrue((await source.Endpoints.GetUserDevicesAsync(token)).All(device => device.IsCurrentDevice));
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(assessment.Healthy);
        Assert.IsNotNull(assessment.RecoveryPlanId);
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskOtherDevices");
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskUnmergedData");
        Assert.AreEqual(0, (await JournalsAsync(source)).Count, "Assessment must not perform the proposed destructive recovery.");
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);

        var recovered = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(recovered.Healthy, string.Join(", ", recovered.Findings));
        Assert.IsNotNull(recovered.RecoveryJournalId);
        CollectionAssert.Contains(recovered.Findings.ToArray(), "ConfirmedHistoryRecovery");
        var after = await ReadUserAsync(source, token);
        CollectionAssert.AreEqual(before.EncryptedPayload, after.EncryptedPayload);
        CollectionAssert.AreEqual(before.EncryptedGeneralUserDataPayload, after.EncryptedGeneralUserDataPayload);
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, after.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(otherBefore.IntegrityHash, (await ReadUserAsync(source, other)).IntegrityHash);
        Assert.IsFalse(await source.Endpoints.GetLocalUserSyncOnAsync(token));
        var secret = (await source.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Single();
        CollectionAssert.AreEqual(Secret(), await source.Endpoints.GetUnsecurePasswordAsync(token, secret.Id));
        var archive = await ReadJournalAsync(source, (await JournalsAsync(source)).Single());
        Assert.AreEqual(before.UId, archive.User.UId);
        Assert.AreEqual(recovered.RecoveryJournalId, (Guid?)archive.JournalId);
        Assert.IsTrue(archive.EvidenceOnly);
        Assert.AreEqual(0, archive.User.EncryptedPayload.Length, "A verified local vault copy must not be retained with unresolved remote evidence.");
        Assert.AreEqual(0, archive.User.EncryptedUserPasswordsDataPayload.Length);
        Assert.IsTrue(archive.SavedKey is null or { Length: 0 },
            "Recovery must not create a remembered key when Remember Me was disabled; null and an empty buffer both mean no key.");
        Assert.IsTrue(archive.Snapshots.Any(row => row.Status == UserSyncSnapshotStatus.IsolatedFork && row.EnvelopePayload.SequenceEqual(retainedEnvelope)));
        Assert.IsTrue(archive.Operations.Any(row => Encoding.UTF8.GetString(row.EnvelopePayload) == "{broken history"));
        using (var scope = source.Services.CreateScope())
        {
            var snapshots = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(before.UId);
            Assert.IsTrue(snapshots.Any(row => row.Status == UserSyncSnapshotStatus.SupersededBadEvidence));
        }
        var replay = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(replay.Healthy);
        Assert.IsNull(replay.RecoveryPlanId);
        Assert.AreEqual(1, (await JournalsAsync(source)).Count);
        await EnrollAsync(source, next, token);
        var nextToken = await next.Endpoints.LoginAsync(next.CreateLoginRequest("confirmed_recovery"));
        var transferred = (await next.Endpoints.GetSavedPasswordsAsync(nextToken)).Passwords.Single();
        CollectionAssert.AreEqual(Secret(), await next.Endpoints.GetUnsecurePasswordAsync(nextToken, transferred.Id));
        Assert.AreEqual(0, (await JournalsAsync(next)).Count, "Installation-local journals must never enter enrollment bootstrap data.");
        using (var scope = source.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        Assert.AreEqual(1, (await JournalsAsync(source)).Count, "A healthy profile and fresh enrollment do not resolve a retained fork.");
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReobservingTheSameEvidence_DoesNotInvalidateConfirmedRecovery()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().AddAsync(new UserSyncFault
            {
                UserId = before.UId, Scope = UserSyncFaultScope.ControlPlane,
                Kind = UserSyncFaultKind.MembershipConflict, Status = UserSyncHealthStatus.TerminalConflict,
                KeyEpoch = before.KeyEpoch, MembershipEpoch = before.MembershipEpoch,
                DiagnosticCode = "invalid-removal-history", BlocksLifecycle = true
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsNotNull(assessment.RecoveryPlanId);
        using (var scope = source.Services.CreateScope())
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(1);
            var session = scope.ServiceProvider.GetRequiredService<SqliteSession>();
            await using var transaction = await session.BeginTransactionAsync();
            await scope.ServiceProvider.ExecuteFixtureSqlAsync("""
                UPDATE "UserSyncFaults" SET "LastDetectedAtUtc" = $now, "LastRecoveryAttemptAtUtc" = $now,
                    "NextRecoveryAttemptAtUtc" = $next, "RecoveryAttemptCount" = "RecoveryAttemptCount" + 1
                WHERE "UserId" = $user AND "Scope" = $scope AND "DiagnosticCode" = $code;
                UPDATE "UserControlOperations" SET "ReceivedAtUtc" = $now, "LastReceivedFromDeviceId" = $device WHERE "UserId" = $user;
                UPDATE "UserSyncSnapshots" SET "ReceivedAtUtc" = $now, "LastReceivedFromDeviceId" = $device WHERE "UserId" = $user;
                UPDATE "UserRevisionKnowledge" SET "LastUpdatedAtUtc" = $now WHERE "UserId" = $user;
                UPDATE "UserControlStates" SET "LastUpdatedAtUtc" = $now WHERE "UserId" = $user;
                UPDATE "UserSyncStates" SET "LastUpdatedAtUtc" = $now WHERE "UserId" = $user;
                UPDATE "DeviceEnrollmentCommits" SET "LastAttemptAtUtc" = $now WHERE "UserId" = $user;
                """, ("$now", now), ("$next", now.AddMinutes(5)), ("$scope", (byte)UserSyncFaultScope.ControlPlane),
                ("$code", "invalid-removal-history"), ("$device", removed.Identity.LocalDeviceId), ("$user", before.UId));
            await transaction.CommitAsync();
        }
        await source.Endpoints.GetUserDevicesAsync(token); // The frontend refreshes this before confirmation.
        var result = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        CollectionAssert.DoesNotContain(result.Findings.ToArray(), "RecoveryPlanChanged");
        CollectionAssert.Contains(result.Findings.ToArray(), "ConfirmedHistoryRecovery");
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, (await ReadUserAsync(source, token)).EncryptedUserPasswordsDataPayload);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task ChangedProfile_InvalidatesApprovalWithoutSavingOrResettingAnything()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest { Name = "Newer secret", Password = Secret() });
        var current = await ReadUserAsync(source, token);
        var result = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        CollectionAssert.Contains(result.Findings.ToArray(), "RecoveryPlanChanged");
        Assert.AreNotEqual(assessment.RecoveryPlanId, result.RecoveryPlanId);
        CollectionAssert.AreEqual(current.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task ApprovalForAnotherProfile_CannotResetEitherProfile()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var other = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("other_approval"));
        var a = await ReadUserAsync(source, token);
        var b = await ReadUserAsync(source, other);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var result = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(other, assessment.RecoveryPlanId!, Password());
        CollectionAssert.Contains(result.Findings.ToArray(), "RecoveryPlanChanged");
        CollectionAssert.AreEqual(a.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        CollectionAssert.AreEqual(b.IntegrityHash, (await ReadUserAsync(source, other)).IntegrityHash);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task JournalWriteFailure_LeavesTheDatabaseAndPendingHistoryUntouched()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
            await scope.ServiceProvider.ExecuteFixtureSqlAsync("""DROP TABLE "UserRecoveryJournals";""");
        var result = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsFalse(result.Healthy);
        Assert.IsNull(result.RecoveryPlanId);
        CollectionAssert.Contains(result.Findings.ToArray(), "RecoveryBlockedJournal");
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task MissingLocalAuthorization_CanBeRebuiltAfterExplicitApproval()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("missing_membership"));
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
            authorizations.DeleteRange(await authorizations.ListForUserAsync(before.UId));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskLocalMembership");
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, (await ReadUserAsync(source, token)).EncryptedUserPasswordsDataPayload);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task MissingCheckpoint_RequiresApprovalInsteadOfDiscardingVerifiedLocalPasswords()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("missing_checkpoint"));
        await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest { Name = "Keep", Password = Secret() });
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var checkpoints = scope.ServiceProvider.GetRequiredService<IUserCanonicalCheckpointRepository>();
            checkpoints.Delete((await checkpoints.GetAsync(before.UId))!);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskCheckpoint");
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, (await ReadUserAsync(source, token)).EncryptedUserPasswordsDataPayload);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task InvalidLocalSequence_IsRepairedWithoutResettingMembership()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("repair_sequence"));
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            Assert.AreEqual(1, await scope.ServiceProvider.ExecuteFixtureSqlAsync("""
                UPDATE "UserControlStates" SET "NextOriginSequence" = 0 WHERE "UserId" = $user;
                """, ("$user", before.UId)));
        }
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        CollectionAssert.Contains(result.Findings.ToArray(), "ControlSequenceRepaired");
        var after = await ReadUserAsync(source, token);
        Assert.AreEqual(before.KeyEpoch, after.KeyEpoch);
        Assert.AreEqual(before.MembershipEpoch, after.MembershipEpoch);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [DataRow(UserControlOperationStatus.StoredPending)]
    [DataRow(UserControlOperationStatus.Applied)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task AccountDeletionEvidence_CannotBeBypassedByConfirmation(UserControlOperationStatus status)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("deletion_barrier"));
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().AddAsync(new UserControlOperation
            {
                OperationId = Guid.NewGuid(), UserId = before.UId, OperationType = UserControlOperationType.AccountDeletion,
                OriginDeviceId = source.Identity.LocalDeviceId, OriginInstanceId = source.Identity.OriginInstanceId,
                OriginSequence = 1, PreviousKeyEpoch = before.KeyEpoch, ResultingKeyEpoch = before.KeyEpoch,
                PreviousMembershipEpoch = before.MembershipEpoch, ResultingMembershipEpoch = before.MembershipEpoch,
                Status = status, EnvelopePayload = [1]
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsNull(result.RecoveryPlanId);
        CollectionAssert.Contains(result.Findings.ToArray(), "RecoveryBlockedAccountDeletion");
        var confirmed = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, new string('A', 64), Password());
        Assert.IsNull(confirmed.RecoveryPlanId);
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task WrongMasterPassword_CannotAuthorizeRecoveryOrCreateAJournal()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var before = await ReadUserAsync(source, token);
        var result = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Encoding.UTF8.GetBytes("IncorrectPassword!"));
        CollectionAssert.Contains(result.Findings.ToArray(), "RecoveryPasswordInvalid");
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [DataRow("checkpoint")]
    [DataRow("cutoff")]
    [DataRow("publication")]
    [DataRow("enrollment")]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task UnusableUnsignedCounters_CanBeRecoveredAfterReview(string counter)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var sql = counter switch
            {
                "checkpoint" => """UPDATE "UserCanonicalCheckpoints" SET "CheckpointSequence" = $value WHERE "UserId" = $user;""",
                "cutoff" => """UPDATE "UserOriginRemovalCutoffs" SET "UserKeyEpoch" = $value WHERE "UserId" = $user;""",
                "publication" => """UPDATE "UserSyncStates" SET "NextOriginRevision" = $value WHERE "UserId" = $user;""",
                "enrollment" => """UPDATE "DeviceEnrollmentCommits" SET "Version" = $value WHERE "UserId" = $user;""",
                _ => throw new ArgumentOutOfRangeException(nameof(counter))
            };
            Assert.AreEqual(1, await scope.ServiceProvider.ExecuteFixtureSqlAsync(sql, ("$value", long.MaxValue), ("$user", before.UId)));
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskCounterMetadata");
        Assert.IsNotNull(assessment.RecoveryPlanId);
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, (await ReadUserAsync(source, token)).EncryptedUserPasswordsDataPayload);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ConfirmedRecovery_CommitFailureRollsBackJournalHistoryAndCheckpoint()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var before = await ReadUserAsync(source, token);
        byte[] checkpointSignature;
        using (var scope = source.Services.CreateScope())
        {
            var checkpoints = scope.ServiceProvider.GetRequiredService<IUserCanonicalCheckpointRepository>();
            checkpointSignature = (await checkpoints.GetAsync(before.UId))!.Signature.ToArray();
            var diagnostics = ActivatorUtilities.CreateInstance<SelfDiagnosticsService>(scope.ServiceProvider,
                new FailCommitUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>()));
            await Assert.ThrowsExactlyAsync<IOException>(() => diagnostics.ConfirmRepairAsync(token, assessment.RecoveryPlanId!, Password()));
        }
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
        using (var scope = source.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            CollectionAssert.AreEqual(checkpointSignature,
                (await services.GetRequiredService<IUserCanonicalCheckpointRepository>().GetAsync(before.UId))!.Signature);
            Assert.HasCount(2, await services.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(before.UId));
            Assert.IsNotEmpty(await services.GetRequiredService<IUserOriginRemovalCutoffRepository>().ListForUserAsync(before.UId));
            Assert.IsTrue((await services.GetRequiredService<IDeviceEnrollmentCommitRepository>().ListForUserAsync(before.UId))
                .Any(row => row.Status == DeviceEnrollmentCommitStatus.TransferFailed));
        }
        Assert.AreEqual(0, (await JournalsAsync(source)).Count, "The journal must roll back with the reset; the original profile is intact.");
        var fresh = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, fresh.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
    }

    [TestMethod]
    [DataRow(UserSyncFaultScope.Database)]
    [DataRow(UserSyncFaultScope.LocalCanonical)]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task BlockingDurableFault_IsReportedEvenWhenHistoryAndLocalPasswordsVerify(UserSyncFaultScope scopeKind)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("durable_sync_fault"));
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().AddAsync(new UserSyncFault
            {
                UserId = before.UId, Scope = scopeKind, Kind = UserSyncFaultKind.CanonicalCheckpointMismatch,
                Status = UserSyncHealthStatus.AwaitingEvidence, BlocksPublishing = true, DiagnosticCode = "test-stale-fault"
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(assessment.Healthy);
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
        if (scopeKind == UserSyncFaultScope.Database)
        {
            Assert.IsNull(assessment.RecoveryPlanId);
            CollectionAssert.Contains(assessment.Findings.ToArray(), "RecoveryBlockedDatabase");
        }
        else
        {
            CollectionAssert.Contains(assessment.RecoveryRisks.ToArray(), "RecoveryRiskSyncFault");
            var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
            Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
            CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, (await ReadUserAsync(source, token)).EncryptedUserPasswordsDataPayload);
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task VerifiedRecovery_DeletesTheTemporaryJournalAndCreatesNoRecoveryDirectory()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        Assert.IsNull(repaired.RecoveryJournalId);
        CollectionAssert.Contains(repaired.Findings.ToArray(), "RecoveryJournalCleaned");
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
        Assert.IsFalse(Directory.Exists(RecoveryDirectory(source)), "Recovery must never create an external file or directory.");
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task InterruptedCompletion_IsRetriedFromTheDatabaseAndThenRemoved()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var actual = scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>();
            var runtime = new FakeSyncRuntimeService();
            var diagnostics = ActivatorUtilities.CreateInstance<SelfDiagnosticsService>(scope.ServiceProvider, new InterruptedJournalService(actual), runtime);
            await Assert.ThrowsExactlyAsync<MutationPartiallyCommittedException>(() =>
                diagnostics.ConfirmRepairAsync(token, assessment.RecoveryPlanId!, Password()));
            Assert.AreEqual(1, runtime.RefreshSyncEnabledCalls, "Interrupted cleanup must still reconcile the committed synchronization pause.");
        }
        var row = (await JournalsAsync(source)).Single();
        Assert.AreEqual(UserRecoveryJournalState.PendingVerification, row.State);
        var snapshot = await ReadJournalAsync(source, row);
        Assert.IsFalse(snapshot.EvidenceOnly);
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, snapshot.User.EncryptedUserPasswordsDataPayload);
        using (var scope = source.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
        var after = await ReadUserAsync(source, token);
        CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, after.EncryptedUserPasswordsDataPayload);
        Assert.IsTrue(after.KeyEpoch > before.KeyEpoch);
        Assert.IsFalse(Directory.Exists(RecoveryDirectory(source)));
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task JournalProtectionFailure_RetainsDataAndRetriesWithoutDiscardingIt()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        using (var scope = source.Services.CreateScope())
        {
            var actual = scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>();
            var diagnostics = ActivatorUtilities.CreateInstance<SelfDiagnosticsService>(scope.ServiceProvider, new InterruptedJournalService(actual));
            await Assert.ThrowsExactlyAsync<MutationPartiallyCommittedException>(() => diagnostics.ConfirmRepairAsync(token, assessment.RecoveryPlanId!, Password()));
        }
        var original = (await JournalsAsync(source)).Single();
        using (var scope = source.Services.CreateScope())
        {
            Assert.AreEqual(1, await scope.ServiceProvider.ExecuteFixtureSqlAsync("""
                UPDATE "UserRecoveryJournals" SET "ProtectedKey" = $key WHERE "Id" = $id;
                """, ("$key", new byte[] { 0 }), ("$id", original.Id)));
        }
        using (var scope = source.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        Assert.AreEqual(1, (await JournalsAsync(source)).Count, "An unreadable protected journal is not proof that its data is disposable.");
        using (var scope = source.Services.CreateScope())
        {
            Assert.AreEqual(1, await scope.ServiceProvider.ExecuteFixtureSqlAsync("""
                UPDATE "UserRecoveryJournals" SET "ProtectedKey" = $key WHERE "Id" = $id;
                """, ("$key", original.ProtectedKey), ("$id", original.Id)));
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        }
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task ExistingDatabaseUpgrade_AddsTheJournalIdempotentlyWithoutChangingProfiles()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("journal_schema"));
        var before = await ReadUserAsync(source, token);
        using (var scope = source.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            await services.ExecuteFixtureSqlAsync("""DROP TABLE "UserRecoveryJournals";""");
            await using var connection = services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await AppDatabaseInitializer.InitializeAsync(connection);
            var journals = services.GetRequiredService<IUserRecoveryJournalStore>();
            journals.Add(new UserRecoveryJournal
            {
                Id = Guid.NewGuid(), UserId = before.UId, State = UserRecoveryJournalState.UnresolvedEvidence,
                CreatedAtUtc = DateTimeOffset.UtcNow, ProtectedKey = [1], EncryptedPayload = [2], PayloadHash = new byte[32]
            });
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            await AppDatabaseInitializer.InitializeAsync(connection);
            Assert.HasCount(1, await journals.ListIdsAsync(default));
        }
        CollectionAssert.AreEqual(before.IntegrityHash, (await ReadUserAsync(source, token)).IntegrityHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task ProfileDeletion_RemovesOnlyThatProfilesRecoveryRows()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("delete_journal"));
        var otherToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("keep_journal"));
        var user = await ReadUserAsync(source, token);
        var other = await ReadUserAsync(source, otherToken);
        using (var scope = source.Services.CreateScope())
        {
            var journals = scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalStore>();
            foreach (var id in new[] { user.UId, other.UId })
                journals.Add(new UserRecoveryJournal
                {
                    Id = Guid.NewGuid(), UserId = id, CreatedAtUtc = DateTimeOffset.UtcNow,
                    State = UserRecoveryJournalState.UnresolvedEvidence, ProtectedKey = [1], EncryptedPayload = [2], PayloadHash = new byte[32]
                });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        await source.Endpoints.DeleteUserAccountAsync(token, Password());
        var rows = await JournalsAsync(source);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(other.UId, rows[0].UserId);
        CollectionAssert.AreEqual(other.IntegrityHash, (await ReadUserAsync(source, otherToken)).IntegrityHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    public async Task PendingEvidence_IsRetainedUntilMergedCoverageProvesItIsRedundant()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        var token = await PrepareAsync(source, removed);
        var user = await ReadUserAsync(source, token);
        UserSyncSnapshot retained;
        using (var scope = source.Services.CreateScope())
        {
            var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
            retained = (await snapshots.ListForUserAsync(user.UId)).First();
            retained.Status = UserSyncSnapshotStatus.Pending;
            snapshots.Update(retained);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var assessment = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        var repaired = await source.Endpoints.ConfirmSelfDiagnosticsRepairAsync(token, assessment.RecoveryPlanId!, Password());
        Assert.IsTrue(repaired.Healthy, string.Join(", ", repaired.Findings));
        Assert.AreEqual(1, (await JournalsAsync(source)).Count);
        using (var scope = source.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        Assert.AreEqual(1, (await JournalsAsync(source)).Count, "Retiring a snapshot is not proof of merging it.");
        using (var scope = source.Services.CreateScope())
        {
            // Model the authenticated merge coordinator's durable receipt. No elapsed
            // time, healthy reset, pairing, or snapshot-status shortcut authorizes cleanup.
            await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>().AddAsync(new UserRevisionKnowledge
            {
                UserId = user.UId, OriginDeviceId = retained.OriginDeviceId, OriginInstanceId = retained.OriginInstanceId,
                UserKeyEpoch = retained.UserKeyEpoch, HighestStoredRevision = retained.OriginRevision,
                HighestMergedRevision = retained.OriginRevision, HighestStoredSnapshotHash = retained.SnapshotHash.ToArray(),
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync();
        }
        Assert.AreEqual(0, (await JournalsAsync(source)).Count);
    }

    private sealed class InterruptedJournalService(IUserRecoveryJournalService inner) : IUserRecoveryJournalService
    {
        public Task<Guid> StageAsync(Guid userId, byte[] snapshot, CancellationToken ct = default) => inner.StageAsync(userId, snapshot, ct);
        public Task<bool> CompleteAsync(Guid id, CancellationToken ct = default) => throw new IOException("Injected interruption before verification cleanup.");
        public Task MaintainAsync(CancellationToken ct = default) => inner.MaintainAsync(ct);
    }

    private sealed class LifecycleProbe(IUserLifecycleCoordinator inner) : IUserLifecycleCoordinator
    {
        public bool IsExecuting { get; private set; }
        public Task ExecuteAsync(Guid userId, Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            ExecuteAsync<object?>(userId, async token => { await action(token); return null; }, ct);
        public Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default) =>
            inner.ExecuteAsync(userId, async token =>
            {
                IsExecuting = true;
                try { return await action(token); }
                finally { IsExecuting = false; }
            }, ct);
    }

    private static async Task<Guid> PrepareAsync(ProductionSyncTestHost source, ProductionSyncTestHost removed)
    {
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("confirmed_recovery"));
        await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest { Name = "Keep", Password = Secret() });
        await EnrollAsync(source, removed, token);
        await source.Endpoints.DisconnectUserDeviceAsync(token, removed.Identity.LocalDeviceId, Password());
        var user = await ReadUserAsync(source, token);
        using var scope = source.Services.CreateScope();
        await RecoveryHistoryTestData.BreakRemovedDeviceHistoryAsync(scope.ServiceProvider, user.UId, removed.Identity.LocalDeviceId);
        return token;
    }
    private static async Task EnrollAsync(ProductionSyncTestHost source, ProductionSyncTestHost target, Guid token)
    {
        source.ConnectTo(target); target.ConnectTo(source);
        await source.EnableSyncAsync(token);
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
    }
    private static async Task<User> ReadUserAsync(ProductionSyncTestHost host, Guid token)
    {
        using var scope = host.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token);
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(user.UId))!;
    }
    private static string RecoveryDirectory(ProductionSyncTestHost host) => Path.Combine(host.Services.GetRequiredService<BackendStoragePaths>().RootDirectory, "Recovery");
    private static async Task<List<UserRecoveryJournal>> JournalsAsync(ProductionSyncTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var journals = scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalStore>();
        var rows = new List<UserRecoveryJournal>();
        foreach (var id in await journals.ListIdsAsync(default))
            rows.Add((await journals.ReadPersistedAsync(id, default))!);
        return rows;
    }
    private static byte[] Password() => Encoding.UTF8.GetBytes("P@ssw0rd12345678");
    private static byte[] Secret() => Encoding.UTF8.GetBytes("RetainedSecret123!");
    private sealed class FailCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
            new FailCommitTransaction(await inner.BeginTransactionAsync(ct));
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => inner.SaveChangesAsync(ct);
        public void ClearTrackedChanges() => inner.ClearTrackedChanges();
    }
    private sealed class FailCommitTransaction(IUnitOfWorkTransaction inner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => throw new IOException("Injected failure before commit.");
        public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<UserRecoveryJournalSnapshot> ReadJournalAsync(ProductionSyncTestHost host, UserRecoveryJournal row)
    {
        using var scope = host.Services.CreateScope();
        var raw = scope.ServiceProvider.GetRequiredService<IKeyProtector>().Unprotect(row.ProtectedKey);
        try
        {
            using var key = EncryptionKey.FromRaw(raw);
            var aad = Hashing.SHA256Hash(hash =>
            {
                hash.WriteString("PasswordManagerLocal.DeviceRecoveryJournal.v1");
                hash.Write(row.Id); hash.Write(row.UserId); hash.Write((int)row.State);
            });
            var bytes = await AES256.DecryptAsync(row.EncryptedPayload, key, aad);
            try { return JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot)!; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}
