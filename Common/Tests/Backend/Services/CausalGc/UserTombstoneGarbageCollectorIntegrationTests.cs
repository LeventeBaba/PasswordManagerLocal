using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class UserTombstoneGarbageCollectorIntegrationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    public async Task CollectAsync_AuthenticatedPendingAncestor_CollectsOnlyTheLaterStableDeletion()
    {
        using var scenario = await PendingCausalScenario.CreateAsync();
        var services = scenario.Host.Services;
        var collector = CreateCollector(services, CreateRealPublisher(services));
        var result = await collector.CollectAsync(scenario.UserId, scenario.Key);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(2, result.ExaminedCount);
        Assert.AreEqual(1, result.RemovedCount);
        Assert.AreEqual(1, result.RetainedCount);
        Assert.AreEqual(TombstoneGarbageCollectionReason.PendingSnapshotMerge, result.Diagnostics.Single().Reason);
        var user = (await services.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(scenario.UserId))!;
        using var data = await services.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(user, scenario.Key);
        Assert.AreEqual(scenario.OlderDeletionId, data.UserPasswordsData.DeletedPasswords.Single().Id);
        var retained = await services.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(scenario.UserId);
        Assert.IsTrue(retained.Any(row => row.Id == scenario.PendingRow.Id && row.Status == UserSyncSnapshotStatus.Pending));
        CollectionAssert.AreEqual(scenario.PendingRow.EnvelopePayload, retained.Single(row => row.Id == scenario.PendingRow.Id).EnvelopePayload);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    [DataRow("stored-only")]
    [DataRow("missing-anchor")]
    [DataRow("hash-mismatch")]
    [DataRow("bad-signature")]
    [DataRow("recovery-candidate")]
    [DataRow("isolated-corrupt")]
    [DataRow("terminal-fork")]
    [DataRow("control-conflict")]
    public async Task CollectAsync_UnresolvedOrUnverifiablePendingEvidence_RetainsBothDeletions(string blocker)
    {
        using var scenario = await PendingCausalScenario.CreateAsync();
        var services = scenario.Host.Services;
        var snapshots = services.GetRequiredService<IUserSyncSnapshotRepository>();
        switch (blocker)
        {
            case "stored-only": scenario.PendingKnowledge.HighestMergedRevision = 0; break;
            case "missing-anchor": snapshots.Delete(scenario.LaterAnchorRow); break;
            case "hash-mismatch": scenario.PendingKnowledge.HighestStoredSnapshotHash[0] ^= 1; break;
            case "bad-signature": scenario.PendingRow.OriginSignature[0] ^= 1; break;
            case "recovery-candidate": scenario.PendingRow.Status = UserSyncSnapshotStatus.RecoveryCandidate; break;
            case "isolated-corrupt": scenario.PendingRow.Status = UserSyncSnapshotStatus.IsolatedCorrupt; break;
            case "terminal-fork": scenario.PendingRow.Status = UserSyncSnapshotStatus.IsolatedFork; break;
            case "control-conflict":
                await services.GetRequiredService<IUserControlStateRepository>().AddAsync(new UserControlState
                {
                    UserId = scenario.UserId, HasConflict = true, AppliedKeyEpoch = 2, AppliedMembershipEpoch = 2
                });
                break;
        }
        var result = await CreateCollector(services, CreateRealPublisher(services)).CollectAsync(scenario.UserId, scenario.Key);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(0, result.RemovedCount);
        var user = (await services.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(scenario.UserId))!;
        using var data = await services.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(user, scenario.Key);
        Assert.AreEqual(2, data.UserPasswordsData.DeletedPasswords.Count);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    public async Task PendingEvidenceRules_ArrivalOrderDoesNotChangeTheBlockingOrigin()
    {
        using var scenario = await PendingCausalScenario.CreateAsync();
        var pending = UserSnapshotEvidenceUtil.DeserializeExact(scenario.PendingRow);
        var later = UserSnapshotEvidenceUtil.DeserializeExact(scenario.LaterAnchorRow);
        var reference = new TombstoneCausalReference
        {
            OriginDeviceId = later.OriginDeviceId, OriginInstanceId = later.OriginInstanceId,
            UserKeyEpoch = later.UserKeyEpoch, MembershipEpoch = later.MembershipEpoch, OriginRevision = later.OriginRevision
        };
        var second = UserSnapshotEvidenceUtil.DeserializeExact(scenario.PendingRow);
        second.UserKeyEpoch = 2;
        second.OriginRevision = 3; // A different key namespace has no merged proof.
        scenario.SignPending(second);
        await scenario.Host.Services.GetRequiredService<IUserMembershipAuthorizationService>().VerifySnapshotAuthorAsync(second);
        var knowledge = new Dictionary<(Guid, Guid, long), UserRevisionKnowledge>
        {
            [(pending.OriginDeviceId, pending.OriginInstanceId, pending.UserKeyEpoch)] = scenario.PendingKnowledge
        };
        var receipts = new Dictionary<(Guid, Guid), List<UserSnapshotEnvelope>>
        {
            [(later.OriginDeviceId, later.OriginInstanceId)] = [later]
        };
        Assert.AreEqual(TombstonePendingEvidenceRules.Evaluate(reference, [pending, second], knowledge, receipts),
            TombstonePendingEvidenceRules.Evaluate(reference, [second, pending], knowledge, receipts));
    }

    // Real encrypted bundles, signed envelopes and historical author verification; repositories
    // use the existing fast collector fixture. Epoch 1 ciphertext deliberately remains pending
    // after epoch 2 acquired its logical contents indirectly. No item-absence hint is invented.
    private sealed class PendingCausalScenario : IDisposable, IAsyncDisposable
    {
        private readonly RecoveryTestMaterial _material = new();
        private readonly Key _peerSigningKey = NSec.Cryptography.Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        private readonly BackendTestHost? _fastHost;
        private readonly ProductionSyncTestHost? _sqliteHost;
        private AsyncServiceScope? _scope;
        private ObservedLifecycleCoordinator? _observedLifecycle;
        private PendingCausalScenario(BackendTestHost? fastHost, ProductionSyncTestHost? sqliteHost = null, AsyncServiceScope? scope = null)
        { _fastHost = fastHost; _sqliteHost = sqliteHost; _scope = scope; }
        public BackendTestHost Host => _fastHost ?? throw new InvalidOperationException("This scenario uses SQLite services.");
        public IServiceProvider Services => _scope?.ServiceProvider ?? Host.Services;
        public ProductionSyncTestHost SqliteHost => _sqliteHost ?? throw new InvalidOperationException("This scenario uses the fast fixture.");
        public Task ObserveNextLifecycleAttempt() => (_observedLifecycle ?? throw new InvalidOperationException("Lifecycle observation requires SQLite.")).Arm();
        public async Task CloseScopeForRestartAsync()
        {
            if (_scope is not AsyncServiceScope scope) return;
            await scope.DisposeAsync(); _scope = null;
        }
        public EncryptionKey Key { get; } = EncryptionKey.Create();
        public Guid UserId => _material.UserId;
        public Guid OlderDeletionId { get; } = Guid.NewGuid();
        public UserSyncSnapshot PendingRow { get; private set; } = null!;
        public UserSyncSnapshot LaterAnchorRow { get; private set; } = null!;
        public UserRevisionKnowledge PendingKnowledge { get; private set; } = null!;

        public static async Task<PendingCausalScenario> CreateAsync()
        {
            var scenario = new PendingCausalScenario(new BackendTestHost());
            try { await scenario.InitializeAsync(); return scenario; }
            catch { scenario.Dispose(); throw; }
        }

        public static async Task<PendingCausalScenario> CreateSqliteAsync()
        {
            var host = await ProductionSyncTestHost.CreateAsync(configureServices: services =>
                services.Replace(ServiceDescriptor.Singleton<IUserLifecycleCoordinator>(provider => new ObservedLifecycleCoordinator(
                    new UserLifecycleCoordinator(provider.GetRequiredService<ICriticalControlIngressStore>())))));
            var scope = host.Services.CreateAsyncScope();
            var scenario = new PendingCausalScenario(null, host, scope);
            scenario._observedLifecycle = (ObservedLifecycleCoordinator)host.Services.GetRequiredService<IUserLifecycleCoordinator>();
            try { await scenario.InitializeAsync(); return scenario; }
            catch { await scenario.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync()
        {
            var services = Services;
            var origin = services.GetRequiredService<IDeviceIdentityService>();
            var peer = RecoveryTestMaterial.CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), _peerSigningKey);
            peer.AgreementPublicKey = new byte[32]; peer.FingerprintHex = new string('A', 64);
            var version = RecoveryTestMaterial.Version(1_700_000_000_000, origin.LocalDeviceId, origin.OriginInstanceId);
            using var bundle = _material.CreateBundle("causal-pending", version);
            var olderReference = new TombstoneCausalReference
            {
                OriginDeviceId = origin.LocalDeviceId, OriginInstanceId = origin.OriginInstanceId,
                UserKeyEpoch = 1, MembershipEpoch = 2, OriginRevision = 1
            };
            bundle.UserPasswordsData.DeletedPasswords.Add(new DeletedPasswordData
            {
                Id = OlderDeletionId, Version = version, DeletedAt = DateTime.UnixEpoch, CausalReference = olderReference
            });
            services.GetRequiredService<IUserDataBundleIntegrityService>().RebuildInitialIntegrity(bundle);
            var older = await _material.CreateEnvelopeAsync(bundle, Key, origin, 1, keyEpoch: 1, membershipEpoch: 2);
            var pending = await _material.CreateEnvelopeAsync(bundle, Key, peer, 1, keyEpoch: 1, membershipEpoch: 2,
                coverage: [Coverage(older)]);
            bundle.UserPasswordsData.DeletedPasswords.Add(new DeletedPasswordData
            {
                Id = Guid.NewGuid(), Version = version with { LogicalCounter = 1 }, DeletedAt = DateTime.UnixEpoch,
                CausalReference = new TombstoneCausalReference
                {
                    OriginDeviceId = origin.LocalDeviceId, OriginInstanceId = origin.OriginInstanceId,
                    UserKeyEpoch = 2, MembershipEpoch = 2, OriginRevision = 7
                }
            });
            services.GetRequiredService<IUserDataBundleIntegrityService>().RebuildInitialIntegrity(bundle);
            var later = await _material.CreateEnvelopeAsync(bundle, Key, origin, 7, keyEpoch: 2, membershipEpoch: 2,
                coverage: [Coverage(pending), Coverage(older)]);
            var peerReceipt = await _material.CreateEnvelopeAsync(bundle, Key, peer, 2, keyEpoch: 2, membershipEpoch: 2,
                coverage: [Coverage(later), Coverage(older), Coverage(pending)]);
            var user = await _material.EncryptUserAsync(bundle, Key, keyEpoch: 2, membershipEpoch: 1);
            user.KeyEpoch = 1;
            var membership = services.GetRequiredService<IUserMembershipAuthorizationService>();
            await membership.CreateGenesisAsync(user);
            var additionPayload = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(UserId, 1, 1,
                peer.LocalDeviceId, peer.OriginInstanceId, peer.SignPublicKey, peer.AgreementPublicKey,
                peer.FingerprintHex, peer.DeviceType);
            var addition = new UserControlOperationEnvelope
            {
                OperationId = Guid.NewGuid(), UserId = UserId, OperationType = UserControlOperationType.DeviceAddition,
                OriginDeviceId = origin.LocalDeviceId, OriginInstanceId = origin.OriginInstanceId, OriginSequence = 1,
                PreviousKeyEpoch = 1, ResultingKeyEpoch = 1, PreviousMembershipEpoch = 1, ResultingMembershipEpoch = 2,
                CreatedAtUtc = DateTimeOffset.UtcNow, OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceAdditionPayload(additionPayload)
            };
            UserControlOperationEnvelopeUtil.FillOriginAuthentication(addition, origin);
            await membership.AuthorizeAdditionAsync(additionPayload, addition.OperationId, addition.OperationHash, addition.CreatedAtUtc);
            user.KeyEpoch = 2; user.MembershipEpoch = 2; user.GenerateIntegrityHash();
            if (_sqliteHost is not null)
            {
                var raw = Key.ExportCopy();
                try { user.SavedKey = services.GetRequiredService<IKeyProtector>().Protect(raw); }
                finally { CryptographicOperations.ZeroMemory(raw); }
                user.GenerateIntegrityHash();
            }
            await services.GetRequiredService<IUserRepository>().AddAsync(user);
            await services.GetRequiredService<IUserControlOperationRepository>().AddAsync(UserControlOperationMapping.ToStoredOperation(
                addition, UserControlOperationEnvelopeUtil.Serialize(addition), UserControlOperationStatus.Applied, addition.CreatedAtUtc, addition.CreatedAtUtc));
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            if (_sqliteHost is not null)
            {
                var local = new LocalUserDevice { UserId = UserId, LocalDeviceIdentityId = origin.LocalDeviceId, IsSyncOn = false };
                local.GenerateIntegrityHash(); await services.GetRequiredService<ILocalUserDeviceRepository>().AddAsync(local);
                await services.GetRequiredService<IUserControlStateRepository>().AddAsync(new UserControlState
                {
                    UserId = UserId, LocalOriginInstanceId = origin.OriginInstanceId, AppliedKeyEpoch = 1,
                    AppliedMembershipEpoch = 2, NextOriginSequence = 2
                });
                await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                await services.GetRequiredService<IUserControlOperationWriterService>().CreateAppliedKeyEpochReplacementAsync(user, 1);
                await services.GetRequiredService<IUserCanonicalHealthService>().UpdateCheckpointAsync(user);
                await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            }
            var snapshots = services.GetRequiredService<IUserSyncSnapshotRepository>();
            PendingRow = RecoveryTestMaterial.CreateSnapshotRow(pending, UserSyncSnapshotStatus.Pending);
            LaterAnchorRow = RecoveryTestMaterial.CreateSnapshotRow(later, UserSyncSnapshotStatus.LocalPublished);
            foreach (var row in new[] { RecoveryTestMaterial.CreateSnapshotRow(older, UserSyncSnapshotStatus.LocalPublished),
                         PendingRow, LaterAnchorRow, RecoveryTestMaterial.CreateSnapshotRow(peerReceipt, UserSyncSnapshotStatus.MergedReceipt) })
            {
                await membership.VerifySnapshotAuthorAsync(UserSnapshotEvidenceUtil.DeserializeExact(row));
                await snapshots.AddAsync(row);
                var knowledge = new UserRevisionKnowledge
                {
                    UserId = UserId, OriginDeviceId = row.OriginDeviceId, OriginInstanceId = row.OriginInstanceId,
                    UserKeyEpoch = row.UserKeyEpoch, HighestStoredRevision = row.OriginRevision,
                    HighestStoredSnapshotHash = row.SnapshotHash.ToArray(), HighestMergedRevision = row.OriginRevision
                };
                if (row == PendingRow) PendingKnowledge = knowledge;
                await services.GetRequiredService<IUserRevisionKnowledgeRepository>().AddAsync(knowledge);
            }
            await services.GetRequiredService<IUserSyncStateRepository>().AddAsync(new UserSyncState
            {
                UserId = UserId, LocalOriginInstanceId = origin.OriginInstanceId, NextOriginRevision = 8
            });
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        private static UserSnapshotCoverageEntry Coverage(UserSnapshotEnvelope envelope) => new()
        {
            OriginDeviceId = envelope.OriginDeviceId, OriginInstanceId = envelope.OriginInstanceId,
            UserKeyEpoch = envelope.UserKeyEpoch, OriginRevision = envelope.OriginRevision
        };

        public void SignPending(UserSnapshotEnvelope envelope) => UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope,
            RecoveryTestMaterial.CreateIdentity(envelope.OriginDeviceId, envelope.OriginInstanceId, _peerSigningKey));

        public void SignPeerControl(UserControlOperationEnvelope envelope)
        {
            envelope.OriginDeviceId = PendingRow.OriginDeviceId; envelope.OriginInstanceId = PendingRow.OriginInstanceId;
            UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, RecoveryTestMaterial.CreateIdentity(
                envelope.OriginDeviceId, envelope.OriginInstanceId, _peerSigningKey));
        }

        public void Dispose() { _fastHost?.Dispose(); Key.Dispose(); _peerSigningKey.Dispose(); _material.Dispose(); }
        public async ValueTask DisposeAsync()
        {
            await CloseScopeForRestartAsync();
            if (_sqliteHost is not null) await _sqliteHost.DisposeAsync();
            Dispose();
        }
    }

    // Observes entry only; all locking, reentrancy, control ownership and ingress fencing
    // are performed by the unmodified production coordinator.
    private sealed class ObservedLifecycleCoordinator(UserLifecycleCoordinator inner) : IUserLifecycleCoordinator
    {
        private TaskCompletionSource? _next;
        public Task Arm()
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _next, next, null) is not null)
                throw new InvalidOperationException("A lifecycle observation is already armed.");
            return next.Task;
        }
        private void Observe() => Interlocked.Exchange(ref _next, null)?.TrySetResult();
        public Task ExecuteAsync(Guid userId, Func<CancellationToken, Task> action, CancellationToken ct = default)
        { Observe(); return inner.ExecuteAsync(userId, action, ct); }
        public Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        { Observe(); return inner.ExecuteAsync(userId, action, ct); }
        public Task<T> ExecuteControlAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        { Observe(); return inner.ExecuteControlAsync(userId, action, ct); }
        public bool IsControlOperation(Guid userId) => inner.IsControlOperation(userId);
    }
}
