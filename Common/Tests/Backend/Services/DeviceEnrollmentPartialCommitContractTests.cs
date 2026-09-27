using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceEnrollmentPartialCommitContractTests
{
    private static readonly Guid RemoteDeviceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RemoteOriginInstanceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly byte[] RemoteSignPublicKey = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private static readonly byte[] RemoteAgreementPublicKey = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private const string RemoteTlsFingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task InvalidEnrollmentCodeIsRejectedBeforeAnyEnrollmentCommitExists()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-invalid-code"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        using var service = CreateService(host.Services);

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentException>(() =>
            service.AddDeviceByCodeAsync(token, "not-an-enrollment-code"));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.AreEqual(DeviceEnrollmentErrorCode.InvalidCode, exception.ErrorCode);
        Assert.IsTrue(exception.IsKnownNotCommitted);
        Assert.IsEmpty(commits);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task TargetMissingBeforeCommitLeavesNoDurableEnrollmentCommit()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-target-missing"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = new GetDeviceEnrollmentInfoReply
            {
                Ok = false,
                ErrorCode = DeviceEnrollmentErrorCode.NewDeviceNotFound.ToString(),
                Error = "The target device was not found."
            }
        };
        using var service = CreateService(host.Services, transport);
        var code = CreateRemoteEnrollmentCode();

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentException>(() =>
            service.AddDeviceByCodeAsync(token, code));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.AreEqual(DeviceEnrollmentErrorCode.NewDeviceNotFound, exception.ErrorCode);
        Assert.IsTrue(exception.IsKnownNotCommitted);
        Assert.IsEmpty(commits);
        Assert.AreEqual(1, transport.EnrollmentInfoCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task IdentityMismatchBeforeCommitLeavesNoDurableEnrollmentCommit()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-identity-mismatch"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = CreateSuccessfulEnrollmentInfoReply(new string('C', 64))
        };
        using var service = CreateService(host.Services, transport);
        var code = CreateRemoteEnrollmentCode(new string('A', 64));

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentException>(() =>
            service.AddDeviceByCodeAsync(token, code));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.AreEqual(DeviceEnrollmentErrorCode.DeviceIdentityConflict, exception.ErrorCode);
        Assert.IsTrue(exception.IsKnownNotCommitted);
        Assert.IsEmpty(commits);
        Assert.AreEqual(1, transport.EnrollmentInfoCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task FullEnrollmentSuccessMarksTheAuthoritativeCommitTransferred()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-full-success"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = CreateMatchingEnrollmentInfoReply(),
            EnrollmentCompletionReply = new CompleteDeviceEnrollmentReply { Ok = true }
        };
        using var service = CreateService(host.Services, transport);

        await service.AddDeviceByCodeAsync(token, CreateRemoteEnrollmentCode());
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.HasCount(1, commits);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, commits[0].Status);
        Assert.AreEqual(RemoteDeviceId, commits[0].TargetDeviceId);
        Assert.IsNotNull(commits[0].CompletedAtUtc);
        Assert.AreEqual(1, transport.EnrollmentInfoCalls);
        Assert.AreEqual(1, transport.EnrollmentCompletionCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SnapshotTransferFailurePreservesARecoverablePartialCommit()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-transfer-failure"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = CreateMatchingEnrollmentInfoReply(),
            EnrollmentCompletionException = new IOException("snapshot response was lost")
        };
        using var service = CreateService(host.Services, transport);

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            service.AddDeviceByCodeAsync(token, CreateRemoteEnrollmentCode()));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.HasCount(1, commits);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed, commits[0].Status);
        Assert.AreEqual(commits[0].CommitId, exception.EnrollmentCommitId);
        Assert.AreEqual(RemoteDeviceId, exception.TargetDeviceId);
        Assert.IsTrue(exception.RecoveryAvailable);
        Assert.IsTrue(exception.TransferPending);
        Assert.IsTrue(exception.RequiresSignedRemovalToUndo);
        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.AreEqual(1, transport.EnrollmentCompletionCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TargetIdentityConflictAfterCommitRequiresRecoveryInsteadOfOrdinaryConflict()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-target-conflict"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = CreateMatchingEnrollmentInfoReply(),
            EnrollmentCompletionReply = CreateRejectedCompletionReply(DeviceEnrollmentErrorCode.DeviceIdentityConflict)
        };
        using var service = CreateService(host.Services, transport);

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            service.AddDeviceByCodeAsync(token, CreateRemoteEnrollmentCode()));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.HasCount(1, commits);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed, commits[0].Status);
        Assert.AreEqual(DeviceEnrollmentErrorCode.DeviceIdentityConflict, exception.ErrorCode);
        Assert.AreEqual(commits[0].CommitId, exception.EnrollmentCommitId);
        Assert.IsTrue(exception.RecoveryAvailable);
        Assert.IsFalse(exception.RequiresProcessRestart);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task UnsupportedDatabaseAfterCommitRequiresRecoveryAndProcessRestart()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints>();
        var token = await endpoints.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-unsupported-db"));
        await endpoints.SetLocalUserSyncOnAsync(token, true);
        var user = await host.Services.GetRequiredService<IUserLookupService>()
            .GetAndVerifyUserAsync(token);
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = CreateMatchingEnrollmentInfoReply(),
            EnrollmentCompletionReply = CreateRejectedCompletionReply(DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion)
        };
        using var service = CreateService(host.Services, transport);

        var exception = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            service.AddDeviceByCodeAsync(token, CreateRemoteEnrollmentCode()));
        var commits = await host.Services.GetRequiredService<IDeviceEnrollmentCommitRepository>()
            .ListForUserAsync(user.UId);

        Assert.HasCount(1, commits);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed, commits[0].Status);
        Assert.AreEqual(DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion, exception.ErrorCode);
        Assert.AreEqual(commits[0].CommitId, exception.EnrollmentCommitId);
        Assert.IsTrue(exception.RecoveryAvailable);
        Assert.IsTrue(exception.RequiresProcessRestart);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void EnrollmentExceptionRequiresExplicitRollbackCertaintyForUnknownFailures()
    {
        var uncertain = new DeviceEnrollmentException("uncertain transaction outcome");
        var rolledBack = new DeviceEnrollmentException(
            DeviceEnrollmentErrorCode.Unknown,
            "known rollback",
            isKnownNotCommitted: true);

        Assert.IsFalse(uncertain.IsKnownNotCommitted);
        Assert.IsTrue(rolledBack.IsKnownNotCommitted);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void PartialCommitExceptionCarriesOnlyImmutableRecoveryFacts()
    {
        var commitId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var targetDeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var targetOriginInstanceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var exception = new DeviceEnrollmentPartiallyCommittedException(
            DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
            "post-commit failure",
            commitId,
            targetDeviceId,
            targetOriginInstanceId,
            recoveryAvailable: true,
            transferPending: true,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart: true);

        Assert.AreEqual(DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion, exception.ErrorCode);
        Assert.AreEqual(commitId, exception.EnrollmentCommitId);
        Assert.AreEqual(targetDeviceId, exception.TargetDeviceId);
        Assert.AreEqual(targetOriginInstanceId, exception.TargetOriginInstanceId);
        Assert.IsTrue(exception.RecoveryAvailable);
        Assert.IsTrue(exception.TransferPending);
        Assert.IsTrue(exception.RequiresSignedRemovalToUndo);
        Assert.IsTrue(exception.RequiresProcessRestart);
    }

    private static string CreateRemoteEnrollmentCode(string? fingerprint = null) =>
        DeviceEnrollmentCode.Create(new DeviceEnrollmentDirectEndpointInfo
        {
            DeviceId = RemoteDeviceId,
            OriginInstanceId = RemoteOriginInstanceId,
            DeviceType = DeviceType.WindowsPc,
            TlsCertFingerprint = fingerprint ?? RemoteTlsFingerprint,
            SignPublicKey = RemoteSignPublicKey.ToArray(),
            AgreementPublicKey = RemoteAgreementPublicKey.ToArray(),
            Port = 26688,
            Hosts = ["192.168.1.50"]
        }).Code;

    private static GetDeviceEnrollmentInfoReply CreateMatchingEnrollmentInfoReply() =>
        new()
        {
            Ok = true,
            DeviceId = RemoteDeviceId.ToString("N"),
            OriginInstanceId = RemoteOriginInstanceId.ToString("N"),
            DeviceType = (uint)DeviceType.WindowsPc,
            TlsCertFingerprint = RemoteTlsFingerprint,
            SignPub = ByteString.CopyFrom(RemoteSignPublicKey),
            AgreementPub = ByteString.CopyFrom(RemoteAgreementPublicKey)
        };

    private static GetDeviceEnrollmentInfoReply CreateSuccessfulEnrollmentInfoReply(string fingerprint) =>
        new()
        {
            Ok = true,
            DeviceId = Guid.Parse("66666666-6666-6666-6666-666666666666").ToString("N"),
            OriginInstanceId = Guid.Parse("77777777-7777-7777-7777-777777777777").ToString("N"),
            DeviceType = (uint)DeviceType.WindowsPc,
            TlsCertFingerprint = fingerprint,
            SignPub = ByteString.CopyFrom(Enumerable.Repeat((byte)0x61, 32).ToArray()),
            AgreementPub = ByteString.CopyFrom(Enumerable.Repeat((byte)0x62, 32).ToArray())
        };

    private static CompleteDeviceEnrollmentReply CreateRejectedCompletionReply(DeviceEnrollmentErrorCode errorCode) =>
        new()
        {
            Ok = false,
            ErrorCode = errorCode.ToString(),
            Error = "The target rejected the enrollment import."
        };

    private static DeviceEnrollmentService CreateService(
        IServiceProvider services,
        FakeSyncTransportClientService? transport = null)
    {
        var identity = services.GetRequiredService<IDeviceIdentityService>();
        var endpointRegistry = services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>();
        var runtime = services.GetRequiredService<ISyncRuntimeService>();
        transport ??= new FakeSyncTransportClientService();
        var networkAddresses = new FakeLocalNetworkAddressService();
        var snapshotService = new EnrollmentContractSnapshotService(new DeviceEnrollmentSnapshotService(identity));
        var localLinks = new DeviceEnrollmentLocalLinkService(identity);
        var executionProfileProvider = new FakeBackendExecutionProfileProvider();
        executionProfileProvider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35)),
            isInteractive: true);
        var service = new DeviceEnrollmentService(
            services.GetRequiredService<IServiceScopeFactory>(),
            identity,
            endpointRegistry,
            runtime,
            new FakeLocalDiscoveryService(),
            new DeviceEnrollmentEndpointService(identity, transport, networkAddresses),
            new DeviceEnrollmentRegistrationService(identity, endpointRegistry, networkAddresses, localLinks),
            snapshotService,
            new DeviceEnrollmentSnapshotTransferService(identity, transport, snapshotService),
            new DeviceEnrollmentSnapshotImporterService(identity, localLinks),
            services.GetRequiredService<IInteractiveUserDataStateAccessor>(),
            executionProfileProvider);
        service.OpenInteractiveAdmission();
        return service;
    }
}
