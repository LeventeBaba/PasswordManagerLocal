using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using NSecKey = NSec.Cryptography.Key;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("EvidenceCompaction")]
public sealed class RecoveredOriginFaultCompactionTests
{
    [TestMethod]
    [TestCategory("StorageBudget")]
    public async Task VerifiedSupersession_ReclaimsOnlyDiagnosticText_AndKeepsReplayForkAndRecoveryEvidence()
    {
        await using var fixture = await FaultFixture.CreateAsync();
        var before = await SqliteStorageMeasurement.CaptureAsync(fixture.Db.Connections, "");
        using var session = fixture.Db.OpenSession();
        var membership = fixture.MembershipFor(session);
        var analyzer = new SqliteStorageRetentionAnalyzer(session, membership);
        var finding = (await analyzer.AnalyzePageAsync("UserSyncFaults", fixture.UserId)).Records.Single();
        Assert.AreEqual(StorageRetentionReason.SafeToCompact, finding.Reason);
        Assert.IsTrue(finding.ReclaimableBytes > 0);
        Assert.AreEqual(fixture.Fault.DiagnosticCode,
            (await new UserSyncFaultRepository(session).GetByIdAsync(fixture.Fault.Id, default))!.DiagnosticCode);
        var compactor = new SqliteSynchronizationEvidenceCompactor(session, fixture.Lifecycle, membership);
        Assert.AreEqual(finding.ReclaimableBytes, await compactor.CompactAsync(fixture.UserId));
        Assert.AreEqual(0L, await compactor.CompactAsync(fixture.UserId));
        var after = await SqliteStorageMeasurement.CaptureAsync(fixture.Db.Connections, "");
        Assert.AreEqual(before.ChargedBytes["Recovery"] - finding.ReclaimableBytes, after.ChargedBytes["Recovery"]);
        Assert.AreEqual(before.Rows["UserSyncFaults"], after.Rows["UserSyncFaults"]);
        using var reopened = fixture.Db.OpenSession();
        var retained = (await new UserSyncFaultRepository(reopened).GetByIdAsync(fixture.Fault.Id, default))!;
        Assert.AreEqual(fixture.Fault.AffectedComponent, retained.AffectedComponent); Assert.AreEqual("", retained.DiagnosticCode);
        Assert.IsTrue(RecoveredOriginFaultCompactionPolicy.SameCapture(fixture.Fault, WithDiagnosticsOf(fixture.Fault)),
            "Every non-diagnostic field must remain exact.");
        var envelope = fixture.HealthyEnvelope;
        var inbox = new UserSnapshotInboxService(new UserRepository(reopened), new UserSyncSnapshotRepository(reopened),
            new UserRevisionKnowledgeRepository(reopened), new FakeDeviceIdentityService { LocalDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid() },
            reopened, fixture.Lifecycle, fixture.MembershipFor(reopened), syncFaults: new UserSyncFaultService(new UserSyncFaultRepository(reopened)));
        Assert.AreEqual(UserSnapshotReceiptState.ObsoleteRevision,
            (await inbox.StoreAsync(fixture.OlderEnvelope, Guid.NewGuid())).State);
        // Preserve same-revision fork detection after ordinary diagnostic reduction.
        var fork = UserSnapshotEvidenceUtil.DeserializeExact(RecoveryTestMaterial.CreateSnapshotRow(envelope));
        fork.CreatedAtUtc = fork.CreatedAtUtc.AddMilliseconds(1);
        fork.User.IntegrityHash = SyncCryptoUtil.CalculateUserHash(fork.User, fork.CreatedAtUtc.ToUnixTimeMilliseconds());
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(fork, fixture.Origin);
        Assert.AreEqual(UserSnapshotReceiptState.Quarantined, (await inbox.StoreAsync(fork, Guid.NewGuid())).State);
        Assert.IsTrue(await new UserSyncFaultRepository(reopened).HasTerminalOriginFaultAsync(
            fixture.UserId, fixture.Origin.LocalDeviceId, fixture.Origin.OriginInstanceId, 1));

        UserSyncFault WithDiagnosticsOf(UserSyncFault source)
        {
            retained.AffectedComponent = source.AffectedComponent; retained.DiagnosticCode = source.DiagnosticCode;
            return retained;
        }
    }

    [TestMethod]
    [DataRow("terminal")]
    [DataRow("conflicting-hash")]
    [DataRow("stored-only")]
    [DataRow("missing-exact-replacement")]
    [DataRow("bad-envelope")]
    [DataRow("recovery-candidate")]
    [DataRow("unresolved-journal")]
    [DataRow("active-conflict")]
    [DataRow("replacement-history")]
    [DataRow("removal-cutoff")]
    public async Task MissingOrConflictingDependencies_KeepOriginalFaultDiagnostics(string blocker)
    {
        await using var fixture = await FaultFixture.CreateAsync();
        var fault = (await fixture.Db.UserSyncFaults.ListForUserAsync(fixture.UserId)).Single();
        switch (blocker)
        {
            case "terminal": fault.Status = UserSyncHealthStatus.TerminalConflict; fixture.Db.UserSyncFaults.Update(fault); break;
            case "conflicting-hash": fault.ConflictingHash = Hashing.SHA256Hash([99]); fixture.Db.UserSyncFaults.Update(fault); break;
            case "stored-only":
                fixture.Knowledge.HighestMergedRevision = 5; fixture.Db.UserRevisionKnowledge.Update(fixture.Knowledge); break;
            case "missing-exact-replacement": fixture.Db.UserSyncSnapshots.Delete(fixture.HealthyRow); break;
            case "bad-envelope": fixture.HealthyRow.OriginSignature[0] ^= 1; fixture.Db.UserSyncSnapshots.Update(fixture.HealthyRow); break;
            case "recovery-candidate": fixture.HealthyRow.Status = UserSyncSnapshotStatus.RecoveryCandidate; fixture.Db.UserSyncSnapshots.Update(fixture.HealthyRow); break;
            case "unresolved-journal":
                new UserRecoveryJournalStore(fixture.Db.UnitOfWork).Add(new UserRecoveryJournal
                {
                    UserId = fixture.UserId, ProtectedKey = [1], EncryptedPayload = [2], PayloadHash = new byte[32],
                    State = UserRecoveryJournalState.UnresolvedEvidence
                });
                break;
            case "active-conflict":
                await fixture.Db.UserSyncFaults.AddAsync(new UserSyncFault
                {
                    UserId = fixture.UserId, Scope = UserSyncFaultScope.ControlPlane, Kind = UserSyncFaultKind.MembershipConflict,
                    Status = UserSyncHealthStatus.TerminalConflict
                });
                break;
            case "replacement-history":
                var user = (await fixture.Db.Users.GetByIdAsync(fixture.UserId))!;
                user.KeyEpoch = 2; user.GenerateIntegrityHash(); fixture.Db.Users.Update(user);
                break;
            case "removal-cutoff":
                var authorization = (await fixture.Db.UserMembershipAuthorizations.ListForUserAsync(fixture.UserId)).Single();
                authorization.IsActive = false; authorization.EndedMembershipEpoch = 2; authorization.MaximumKeyEpoch = 1;
                authorization.RemovalOperationId = Guid.NewGuid(); authorization.RemovalOperationHash = new byte[32];
                fixture.Db.UserMembershipAuthorizations.Update(authorization);
                await fixture.Db.UserOriginRemovalCutoffs.AddAsync(new UserOriginRemovalCutoff
                {
                    UserId = fixture.UserId, DeviceId = fixture.Origin.LocalDeviceId, OriginInstanceId = fixture.Origin.OriginInstanceId,
                    UserKeyEpoch = 1, AuthorizationId = authorization.AuthorizationId,
                    RemovalOperationId = authorization.RemovalOperationId!.Value, RemovalOperationHash = authorization.RemovalOperationHash!.ToArray(),
                    ResultingMembershipEpoch = 2, HighestAcceptedSnapshotRevision = 5
                });
                break;
        }
        await fixture.Db.UnitOfWork.SaveChangesAsync();
        using var session = fixture.Db.OpenSession();
        Assert.AreEqual(0L, await new SqliteSynchronizationEvidenceCompactor(session, fixture.Lifecycle,
            fixture.MembershipFor(session)).CompactAsync());
        var retained = (await new UserSyncFaultRepository(session).GetByIdAsync(fault.Id, default))!;
        Assert.AreEqual(fault.DiagnosticCode, retained.DiagnosticCode);
        CollectionAssert.AreEqual(fault.ObservedHash, retained.ObservedHash);
    }

    [TestMethod]
    public async Task FaultWriteFailureRollsBack_AndConcurrentRecoveryStateIsRevalidated()
    {
        await using var fixture = await FaultFixture.CreateAsync();
        using (var command = await fixture.Db.UnitOfWork.CreateCommandAsync("""
            CREATE TRIGGER RejectFaultCompaction AFTER UPDATE OF DiagnosticCode ON UserSyncFaults
            BEGIN SELECT RAISE(ABORT, 'injected-fault-compaction-failure'); END;
            """)) await command.ExecuteNonQueryAsync();
        using var session = fixture.Db.OpenSession();
        var compactor = new SqliteSynchronizationEvidenceCompactor(session, fixture.Lifecycle, fixture.MembershipFor(session));
        await Assert.ThrowsExactlyAsync<SqliteException>(() => compactor.CompactAsync());
        Assert.AreEqual(fixture.Fault.DiagnosticCode, (await new UserSyncFaultRepository(session).GetByIdAsync(fixture.Fault.Id, default))!.DiagnosticCode);
        using (var command = await fixture.Db.UnitOfWork.CreateCommandAsync("DROP TRIGGER RejectFaultCompaction;")) await command.ExecuteNonQueryAsync();
        var lifecycle = new BeforeFaultEntry(fixture.Lifecycle, async () =>
        {
            fixture.HealthyRow.Status = UserSyncSnapshotStatus.RecoveryCandidate;
            fixture.Db.UserSyncSnapshots.Update(fixture.HealthyRow); await fixture.Db.UnitOfWork.SaveChangesAsync();
        });
        Assert.AreEqual(0L, await new SqliteSynchronizationEvidenceCompactor(session, lifecycle, fixture.MembershipFor(session)).CompactAsync());
        Assert.AreEqual(fixture.Fault.DiagnosticCode, (await new UserSyncFaultRepository(session).GetByIdAsync(fixture.Fault.Id, default))!.DiagnosticCode);
    }

    private sealed class BeforeFaultEntry(IUserLifecycleCoordinator inner, Func<Task> change) : IUserLifecycleCoordinator
    {
        public async Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            await change(); return await inner.ExecuteAsync(userId, action, ct);
        }
        public Task ExecuteAsync(Guid userId, Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            ExecuteAsync<object?>(userId, async token => { await action(token); return null; }, ct);
    }

    private sealed class FaultFixture : IAsyncDisposable
    {
        private readonly RecoveryTestMaterial _material = new();
        private readonly NSecKey _signing = NSecKey.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        private readonly EncryptionKey _key = EncryptionKey.Create();
        public required SqliteRepositoryTestDatabase Db { get; init; }
        public Guid UserId => _material.UserId;
        public FakeDeviceIdentityService Origin { get; private set; } = null!;
        public UserLifecycleCoordinator Lifecycle { get; } = new();
        public UserSyncFault Fault { get; private set; } = null!;
        public UserSyncSnapshot HealthyRow { get; private set; } = null!;
        public UserSnapshotEnvelope HealthyEnvelope { get; private set; } = null!;
        public UserSnapshotEnvelope OlderEnvelope { get; private set; } = null!;
        public UserRevisionKnowledge Knowledge { get; private set; } = null!;
        public UserMembershipAuthorizationService MembershipFor(SqliteSession session) => new(
            new UserMembershipAuthorizationRepository(session), new UserOriginRemovalCutoffRepository(session), Origin);

        public static async Task<FaultFixture> CreateAsync()
        {
            var fixture = new FaultFixture { Db = await SqliteRepositoryTestDatabase.CreateAsync() };
            try { await fixture.InitializeAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync()
        {
            Origin = RecoveryTestMaterial.CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), _signing);
            Origin.AgreementPublicKey = new byte[32]; Origin.FingerprintHex = new string('B', 64);
            using var bundle = _material.CreateBundle("fault-compaction", RecoveryTestMaterial.Version(1000, Origin.LocalDeviceId, Origin.OriginInstanceId));
            var user = await _material.EncryptUserAsync(bundle, _key);
            await Db.Users.AddAsync(user); await Db.UnitOfWork.SaveChangesAsync();
            await MembershipFor(Db.UnitOfWork).CreateGenesisAsync(user); await Db.UnitOfWork.SaveChangesAsync();
            OlderEnvelope = await _material.CreateEnvelopeAsync(bundle, _key, Origin, 5);
            HealthyEnvelope = await _material.CreateEnvelopeAsync(bundle, _key, Origin, 6);
            HealthyRow = RecoveryTestMaterial.CreateSnapshotRow(HealthyEnvelope, UserSyncSnapshotStatus.MergedReceipt);
            await Db.UserSyncSnapshots.AddAsync(HealthyRow);
            Knowledge = new UserRevisionKnowledge
            {
                UserId = UserId, OriginDeviceId = Origin.LocalDeviceId, OriginInstanceId = Origin.OriginInstanceId,
                UserKeyEpoch = 1, HighestStoredRevision = 6, HighestMergedRevision = 6,
                HighestStoredSnapshotHash = HealthyRow.SnapshotHash.ToArray()
            };
            await Db.UserRevisionKnowledge.AddAsync(Knowledge);
            var faults = new UserSyncFaultService(Db.UserSyncFaults);
            await faults.RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = UserId, Scope = UserSyncFaultScope.SnapshotOrigin, Kind = UserSyncFaultKind.IncomingIntegrityFailure,
                Status = UserSyncHealthStatus.Isolated, OriginDeviceId = Origin.LocalDeviceId, OriginInstanceId = Origin.OriginInstanceId,
                KeyEpoch = 1, MembershipEpoch = 1, OriginRevision = 5, ObservedHash = OlderEnvelope.SnapshotHash.ToArray(),
                ExpectedHash = Hashing.SHA256Hash([7]), AffectedComponent = "Passwords", DiagnosticCode = "helyreállított-hibás-forrás", BlocksMerge = true
            });
            await Db.UnitOfWork.SaveChangesAsync();
            await faults.MarkOriginRecoveredAsync(UserId, Origin.LocalDeviceId, Origin.OriginInstanceId, 1, 6);
            await Db.UnitOfWork.SaveChangesAsync();
            Fault = (await Db.UserSyncFaults.ListForUserAsync(UserId)).Single();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); _key.Dispose(); _signing.Dispose(); _material.Dispose();
        }
    }
}
