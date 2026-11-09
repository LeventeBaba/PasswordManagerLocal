using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class DeviceEnrollmentEndToEndTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task OldEnrollmentCompletion_CannotConsumeReplacementCode(bool failOldImport)
    {
        ImportGate gate = null!;
        await using var target = await ProductionEnrollmentTestHost.CreateAsync(inner => gate = new ImportGate(inner, failOldImport));
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var replacementSource = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);
        Connect(replacementSource, target);
        var oldToken = await CreateEnrollmentProfileAsync(source, "old_enrollment");
        var replacementToken = await CreateEnrollmentProfileAsync(replacementSource, "new_enrollment");
        var oldUser = (await GetOnlyUserAsync(source)).UId;
        var replacementUser = (await GetOnlyUserAsync(replacementSource)).UId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var original = await target.Endpoints.StartDeviceEnrollmentAsync(deadline.Token);
        var oldTransfer = TryEnrollAsync(source, oldToken, original.Code, deadline.Token);
        string replacementCode;
        try
        {
            await gate.FirstEntry.Task.WaitAsync(deadline.Token);
            replacementCode = (await target.Endpoints.StartDeviceEnrollmentAsync(deadline.Token)).Code;
            Assert.AreNotEqual(original.Code, replacementCode);
        }
        finally
        {
            gate.ReleaseFirst.TrySetResult();
            await oldTransfer.WaitAsync(deadline.Token);
        }

        Assert.AreEqual(!failOldImport, oldTransfer.Result, "The first transfer must report its actual import outcome.");
        Assert.AreEqual(!failOldImport, (await GetUserIdsAsync(target)).Contains(oldUser));
        Assert.AreEqual(DeviceEnrollmentState.Waiting, (await target.Endpoints.GetDeviceEnrollmentStatusAsync()).State,
            "The previous transfer must leave the replacement session open.");
        if (failOldImport)
            await AssertOnlyReplacementAttemptsCountAsync(target, replacementCode, deadline.Token);
        await replacementSource.Endpoints.AddDeviceByCodeAsync(replacementToken, replacementCode, deadline.Token);
        CollectionAssert.AreEquivalent(failOldImport ? new[] { replacementUser } : new[] { oldUser, replacementUser },
            (await GetUserIdsAsync(target)).ToArray());
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task ConcurrentValidTransfers_OneDisplayedCodeImportsAtMostOneProfile()
    {
        ImportGate gate = null!;
        await using var target = await ProductionEnrollmentTestHost.CreateAsync(inner => gate = new ImportGate(inner, false));
        await using var first = await ProductionEnrollmentTestHost.CreateAsync();
        await using var second = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(first, target);
        Connect(second, target);
        var firstToken = await CreateEnrollmentProfileAsync(first, "first_enrollment");
        var secondToken = await CreateEnrollmentProfileAsync(second, "second_enrollment");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var code = await target.Endpoints.StartDeviceEnrollmentAsync(deadline.Token);
        var firstTransfer = TryEnrollAsync(first, firstToken, code.Code, deadline.Token);
        Task<bool>? secondTransfer = null;
        try
        {
            await gate.FirstEntry.Task.WaitAsync(deadline.Token);
            secondTransfer = TryEnrollAsync(second, secondToken, code.Code, deadline.Token);
            // A second validated import or a definitive rejection resolves the contention.
            await Task.WhenAny(secondTransfer, gate.SecondEntry.Task).WaitAsync(deadline.Token);
        }
        finally
        {
            gate.ReleaseFirst.TrySetResult();
            await firstTransfer.WaitAsync(deadline.Token);
            if (secondTransfer is not null)
                await secondTransfer.WaitAsync(deadline.Token);
        }

        Assert.IsNotNull(secondTransfer);
        Assert.AreNotEqual(firstTransfer.Result, secondTransfer.Result, "Exactly one distinct transfer must succeed.");
        var winner = firstTransfer.Result ? first : second;
        var loser = firstTransfer.Result ? second : first;
        var winnerUser = (await GetOnlyUserAsync(winner)).UId;
        var loserUser = (await GetOnlyUserAsync(loser)).UId;
        CollectionAssert.AreEqual(new[] { winnerUser }, (await GetUserIdsAsync(target)).ToArray());
        using var scope = target.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.IsNull(await services.GetRequiredService<IDeviceRepository>().GetByIdAsNoTrackingAsync(loser.Identity.LocalDeviceId));
        Assert.AreEqual(0, (await services.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(loserUser)).Count);
        Assert.AreEqual(0, (await services.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(loserUser)).Count);
        Assert.AreEqual(DeviceEnrollmentState.Completed, (await target.Endpoints.GetDeviceEnrollmentStatusAsync()).State);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task FailedImport_ReleasesItsReservationForRetryWithTheSameCode()
    {
        ImportGate gate = null!;
        await using var target = await ProductionEnrollmentTestHost.CreateAsync(inner => gate = new ImportGate(inner, true));
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);
        var token = await CreateEnrollmentProfileAsync(source, "retry_reserved_code");
        var userId = (await GetOnlyUserAsync(source)).UId;
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        gate.ReleaseFirst.TrySetResult();

        Assert.IsFalse(await TryEnrollAsync(source, token, code.Code, ct));
        var failure = await GetOnlyEnrollmentCommitAsync(source);
        await AssertNoImportedProfileAsync(target, userId, source.Identity.LocalDeviceId);

        Assert.IsTrue(await TryEnrollAsync(source, token, code.Code, ct));
        var success = await GetOnlyEnrollmentCommitAsync(source);
        Assert.AreEqual(failure.CommitId, success.CommitId);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, success.Status);
        CollectionAssert.AreEqual(new[] { userId }, (await GetUserIdsAsync(target)).ToArray());
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task AdmittedEnrollment_DrainsToAnHonestCommitWhenInteractiveAdmissionCloses()
    {
        ImportGate gate = null!;
        await using var target = await ProductionEnrollmentTestHost.CreateAsync(inner => gate = new ImportGate(inner, false));
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);
        var token = await CreateEnrollmentProfileAsync(source, "draining_enrollment");
        var userId = (await GetOnlyUserAsync(source)).UId;
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var transfer = TryEnrollAsync(source, token, code.Code, ct);
        Task? closing = null;
        try
        {
            await gate.FirstEntry.Task.WaitAsync(ct);
            closing = target.Services.GetRequiredService<DeviceEnrollmentService>().CloseInteractiveAdmissionAsync(ct);
            Assert.IsFalse(closing.IsCompleted, "Closing admission must wait for the admitted import to drain.");
        }
        finally
        {
            gate.ReleaseFirst.TrySetResult();
            await transfer.WaitAsync(ct);
            if (closing is not null)
                await closing.WaitAsync(ct);
        }

        Assert.IsTrue(await transfer);
        CollectionAssert.AreEqual(new[] { userId }, (await GetUserIdsAsync(target)).ToArray());
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, (await GetOnlyEnrollmentCommitAsync(source)).Status);
    }

    private static async Task AssertOnlyReplacementAttemptsCountAsync(
        ProductionEnrollmentTestHost target, string replacementCode, CancellationToken ct)
    {
        var parsed = DeviceEnrollmentCode.Parse(replacementCode);
        try
        {
            var wrongProof = BuildEnrollmentInfoRequest(parsed);
            wrongProof.CodeProof = ByteString.CopyFrom(new byte[32]);
            for (var i = 1; i < SyncConstants.MaxEnrollmentValidationAttempts; i++)
                Assert.IsFalse((await target.ProtocolHandler.GetDeviceEnrollmentInfoAsync(wrongProof, ct)).Ok);

            var genuine = await target.ProtocolHandler.GetDeviceEnrollmentInfoAsync(BuildEnrollmentInfoRequest(parsed), ct);
            Assert.IsTrue(genuine.Ok, "A failed import for the old code must not consume an attempt for the replacement.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    private static async Task<Guid> CreateEnrollmentProfileAsync(ProductionEnrollmentTestHost host, string username)
    {
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        await host.Endpoints.SetLocalUserSyncOnAsync(token, true);
        return token;
    }

    private static async Task<bool> TryEnrollAsync(ProductionEnrollmentTestHost source, Guid token, string code, CancellationToken ct)
    {
        try
        {
            await source.Endpoints.AddDeviceByCodeAsync(token, code, ct);
            return true;
        }
        catch (DeviceEnrollmentPartiallyCommittedException ex) when (ex.ErrorCode is
            DeviceEnrollmentErrorCode.ProfileDataInvalid or DeviceEnrollmentErrorCode.CodeProofInvalid or
            DeviceEnrollmentErrorCode.NewDeviceRejected)
        {
            return false;
        }
    }

    private sealed class ImportGate(IDeviceEnrollmentSnapshotImporterService inner, bool failFirst)
        : IDeviceEnrollmentSnapshotImporterService
    {
        private int _entries;
        public TaskCompletionSource FirstEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ImportAsync(IServiceProvider services, DeviceEnrollmentSnapshot snapshot, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _entries) == 1)
            {
                FirstEntry.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
                if (failFirst)
                    throw new InvalidDataException("Controlled test import failure.");
            }
            else
            {
                SecondEntry.TrySetResult();
            }

            await inner.ImportAsync(services, snapshot, ct);
        }
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("conflicting-origin")]
    [DataRow("cancel-before-end")]
    [DataRow("close-admission")]
    [DataRow("lost-reply")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task TcpEnrollment_InterruptionAndRetryPreserveCommitBoundary(string fault)
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        Connect(source, target);
        const string username = "wire_enrollment";
        var token = await CreateEnrollmentProfileAsync(source, username);
        var userId = (await GetOnlyUserAsync(source)).UId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
        var client = CreateEnrollmentTcpClient(source);
        var address = listener.Endpoint.Address.ToString();
        var port = listener.Endpoint.Port;

        source.Transport.EnrollmentCompletionOverride = async (chunks, cancellation) =>
        {
            if (fault == "lost-reply")
            {
                var reply = await client.CompleteDeviceEnrollmentStreamAsync(address, port, target.Identity.FingerprintHex, chunks, cancellation);
                Assert.IsTrue(reply.Ok, "The real receiver must confirm the import before simulating a lost caller reply.");
                throw new IOException("Test discarded the committed enrollment reply.");
            }

            await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, target.Identity, source.Identity, cancellation);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentStart,
                new CompleteDeviceEnrollmentStartRequest { SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion }, cancellation);
            CompleteDeviceEnrollmentChunk? first = null;
            await foreach (var chunk in chunks.WithCancellation(cancellation))
            {
                first ??= chunk;
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk, chunk, cancellation);
            }

            Assert.IsNotNull(first);
            if (fault == "truncated")
            {
                // Close the write side after the entire ciphertext but before the required end frame.
                await peer.Stream.ShutdownAsync().WaitAsync(cancellation);
                var frame = await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, cancellation);
                if (frame?.Type == SyncTcpMessageType.CompleteDeviceEnrollmentReply)
                    Assert.IsFalse(frame.Parse(CompleteDeviceEnrollmentReply.Parser).Ok);
                throw new IOException("Test interrupted enrollment before the end frame.");
            }

            switch (fault)
            {
                case "conflicting-origin":
                    await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk,
                        new CompleteDeviceEnrollmentChunk { SourceOriginInstanceId = Guid.NewGuid().ToString("N") }, cancellation);
                    break;
                case "cancel-before-end":
                    await target.Endpoints.CancelDeviceEnrollmentAsync(cancellation);
                    break;
                case "close-admission":
                    await target.Services.GetRequiredService<DeviceEnrollmentService>().CloseInteractiveAdmissionAsync(cancellation);
                    break;
            }
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentEnd, cancellation);
            var rejected = await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentReply, cancellation);
            return rejected.Parse(CompleteDeviceEnrollmentReply.Parser);
        };

        var failure = await Assert.ThrowsExactlyAsync<DeviceEnrollmentPartiallyCommittedException>(() =>
            source.Endpoints.AddDeviceByCodeAsync(token, code.Code, ct));
        var senderCommit = await GetOnlyEnrollmentCommitAsync(source);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.TransferFailed, senderCommit.Status);
        if (fault == "close-admission")
        {
            var unavailable = await Assert.ThrowsExactlyAsync<DeviceEnrollmentException>(() =>
                target.Endpoints.GetDeviceEnrollmentStatusAsync(ct));
            Assert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired, unavailable.ErrorCode);
            target.Services.GetRequiredService<DeviceEnrollmentService>().OpenInteractiveAdmission();
        }

        if (fault == "lost-reply")
            CollectionAssert.AreEqual(new[] { userId }, (await GetUserIdsAsync(target)).ToArray());
        else
            await AssertNoImportedProfileAsync(target, userId, source.Identity.LocalDeviceId,
                fault is "cancel-before-end" or "close-admission" ? DeviceEnrollmentState.Cancelled : DeviceEnrollmentState.Waiting);

        var retry = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        source.Transport.EnrollmentCompletionOverride = (chunks, cancellation) =>
            client.CompleteDeviceEnrollmentStreamAsync(address, port, target.Identity.FingerprintHex, chunks, cancellation);
        await source.Endpoints.AddDeviceByCodeAsync(token, retry.Code, ct);
        CollectionAssert.AreEqual(new[] { userId }, (await GetUserIdsAsync(target)).ToArray());
        var reconciled = await GetOnlyEnrollmentCommitAsync(source);
        Assert.AreEqual(failure.EnrollmentCommitId, reconciled.CommitId);
        Assert.AreEqual(senderCommit.AdditionOperationId, reconciled.AdditionOperationId);
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Transferred, reconciled.Status);
        Assert.AreNotEqual(Guid.Empty, await target.Endpoints.LoginAsync(target.CreateLoginRequest(username), ct));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("SecurityRegression")]
    public async Task TcpEnrollment_UnrelatedSessionIdsDoNotExhaustCurrentCode(bool completionRequest)
    {
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        await using var unrelated = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var parsed = DeviceEnrollmentCode.Parse(code.Code);
        try
        {
            await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
            var client = CreateEnrollmentTcpClient(unrelated);
            var address = listener.Endpoint.Address.ToString();
            for (var i = 0; i < SyncConstants.MaxEnrollmentValidationAttempts; i++)
            {
                var wrongId = Guid.NewGuid().ToString("N");
                if (completionRequest)
                {
                    var rejected = await client.CompleteDeviceEnrollmentStreamAsync(address, listener.Endpoint.Port,
                        target.Identity.FingerprintHex, BuildUnprovenEnrollmentAsync(unrelated, target, wrongId), ct);
                    Assert.IsFalse(rejected.Ok);
                    Assert.AreEqual(DeviceEnrollmentErrorCode.CodeProofInvalid.ToString(), rejected.ErrorCode);
                }
                else
                {
                    var rejected = await client.GetDeviceEnrollmentInfoAsync(address, listener.Endpoint.Port,
                        target.Identity.FingerprintHex, new GetDeviceEnrollmentInfoRequest
                        {
                            SessionId = wrongId,
                            CodeProof = ByteString.CopyFrom(new byte[32]),
                            SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
                        }, ct);
                    Assert.IsFalse(rejected.Ok);
                    Assert.AreEqual(string.Empty, rejected.DeviceId);
                    Assert.AreEqual(0, rejected.SignPub.Length);
                    Assert.AreEqual(0, rejected.AgreementPub.Length);
                }
            }

            Assert.AreEqual(0, (await GetUserIdsAsync(target)).Count);
            Assert.AreEqual(DeviceEnrollmentState.Waiting, (await target.Endpoints.GetDeviceEnrollmentStatusAsync(ct)).State,
                "Requests naming other sessions must leave the valid code usable.");
            var valid = await client.GetDeviceEnrollmentInfoAsync(address, listener.Endpoint.Port, target.Identity.FingerprintHex[..32],
                BuildEnrollmentInfoRequest(parsed), ct);
            Assert.IsTrue(valid.Ok);
            Assert.AreEqual(target.Identity.FingerprintHex, valid.TlsCertFingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task TcpEnrollment_BadProofForActualSessionIsRateLimited()
    {
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var parsed = DeviceEnrollmentCode.Parse(code.Code);
        try
        {
            await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
            var client = CreateEnrollmentTcpClient(source);
            var address = listener.Endpoint.Address.ToString();
            var request = BuildEnrollmentInfoRequest(parsed);
            var badProof = request.CodeProof.ToByteArray();
            badProof[0] ^= 1;
            request.CodeProof = ByteString.CopyFrom(badProof);
            for (var i = 0; i < SyncConstants.MaxEnrollmentValidationAttempts; i++)
            {
                var denied = await client.GetDeviceEnrollmentInfoAsync(address, listener.Endpoint.Port,
                    target.Identity.FingerprintHex, request, ct);
                Assert.IsFalse(denied.Ok);
                Assert.AreEqual(string.Empty, denied.DeviceId);
            }
            Assert.AreEqual(DeviceEnrollmentState.Failed, (await target.Endpoints.GetDeviceEnrollmentStatusAsync(ct)).State);

            var fresh = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
            var freshParsed = DeviceEnrollmentCode.Parse(fresh.Code);
            try
            {
                var allowed = await client.GetDeviceEnrollmentInfoAsync(address, listener.Endpoint.Port,
                    target.Identity.FingerprintHex, BuildEnrollmentInfoRequest(freshParsed), ct);
                Assert.IsTrue(allowed.Ok);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(freshParsed.Secret);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task TcpEnrollment_PrefixLookupBindsFullFingerprintToObservedCertificate(bool forgeSuffix)
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var pin = target.Identity.FingerprintHex;
        var advertised = forgeSuffix ? pin[..^1] + (pin[^1] == '0' ? "1" : "0") : pin;
        var server = ReplyWithEnrollmentFingerprintAsync(listener, source, target, advertised, ct);
        var client = CreateEnrollmentTcpClient(source);
        GetDeviceEnrollmentInfoReply? reply = null;
        try
        {
            reply = await client.GetDeviceEnrollmentInfoAsync("127.0.0.1", port, pin[..32],
                new GetDeviceEnrollmentInfoRequest
                {
                    SessionId = "scripted-enrollment-session",
                    CodeProof = ByteString.CopyFrom(new byte[32]),
                    SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
                }, ct);
        }
        catch (Exception ex) when (forgeSuffix && (ex is AuthenticationException || ex is InvalidDataException))
        {
            // An explicit identity error is also an acceptable way to reject the bogus upgrade.
        }
        await server;
        if (forgeSuffix)
            Assert.IsFalse(reply?.Ok == true, "The reply cannot upgrade the code to a fingerprint different from the connected certificate.");
        else
        {
            Assert.IsNotNull(reply);
            Assert.IsTrue(reply.Ok);
            Assert.AreEqual(pin, reply.TlsCertFingerprint);
        }
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task TcpEnrollment_FingerprintPrefixIsAcceptedOnlyForProofGatedInfo()
    {
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var parsed = DeviceEnrollmentCode.Parse(code.Code);
        try
        {
            await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
            var client = CreateEnrollmentTcpClient(source);
            var host = listener.Endpoint.Address.ToString();
            var port = listener.Endpoint.Port;
            var pin = target.Identity.FingerprintHex;
            var wrongPrefix = (pin[0] == '0' ? "1" : "0") + pin[1..32];
            var request = BuildEnrollmentInfoRequest(parsed);
            await AssertPinRejectedAsync(() => client.GetDeviceEnrollmentInfoAsync(host, port, wrongPrefix, request, ct));
            Assert.IsTrue((await client.GetDeviceEnrollmentInfoAsync(host, port, pin, request, ct)).Ok);
            Assert.IsTrue((await client.GetDeviceEnrollmentInfoAsync(host, port, pin[..32], request, ct)).Ok);
            await AssertPinRejectedAsync(() => client.ExchangeUserSnapshotInventoryAsync(host, port, pin[..32], new(), ct));
            await AssertPinRejectedAsync(() => client.CompleteDeviceEnrollmentStreamAsync(host, port, pin[..32], NoEnrollmentChunksAsync(), ct));
            var probe = await client.ProbeAsync(host, port, pin[..32], target.Identity.DeviceIdHex, target.Identity.SignPublicKey, ct);
            Assert.IsFalse(probe.IsSuccess);
            Assert.AreEqual(0, (await GetUserIdsAsync(target)).Count);
            Assert.AreEqual(DeviceEnrollmentState.Waiting, (await target.Endpoints.GetDeviceEnrollmentStatusAsync(ct)).State);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task Enrollment_OwnDisplayedCodeStillCannotEnrollTheSameInstallation()
    {
        await using var host = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var token = await CreateEnrollmentProfileAsync(host, "self_enrollment_guard");
        var code = await host.Endpoints.StartDeviceEnrollmentAsync(deadline.Token);

        await Assert.ThrowsExactlyAsync<DeviceEnrollmentException>(() =>
            host.Endpoints.AddDeviceByCodeAsync(token, code.Code, deadline.Token));
        Assert.AreEqual(0, (await GetEnrollmentCommitsAsync(host)).Count);
        Assert.AreEqual(1, (await GetUserIdsAsync(host)).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityStress")]
    public async Task TcpEnrollment_EnforcesSnapshotByteLimitBeforeImport(bool exceed)
    {
        LoopbackSyncPeer.RequireStressTestsEnabled();
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var parsed = DeviceEnrollmentCode.Parse(code.Code);
        try
        {
            await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
            await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, target.Identity, source.Identity, ct);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentStart,
                new CompleteDeviceEnrollmentStartRequest { SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion }, ct);
            var first = BuildUnprovenEnrollmentChunk(source, target, parsed.SessionId, ByteString.Empty);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk, first, ct);
            const int blockBytes = 1024 * 1024;
            var block = new CompleteDeviceEnrollmentChunk { SnapshotChunk = ByteString.CopyFrom(new byte[blockBytes]) };
            for (var sent = 0; sent < SyncConstants.MaxDeviceEnrollmentSnapshotBytes; sent += blockBytes)
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk, block, ct);
            if (exceed)
            {
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk,
                    new CompleteDeviceEnrollmentChunk { SnapshotChunk = ByteString.CopyFrom(new byte[1]) }, ct);
            }
            else
            {
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentEnd, ct);
            }

            var frame = await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentReply, ct);
            var reply = frame.Parse(CompleteDeviceEnrollmentReply.Parser);
            Assert.IsFalse(reply.Ok);
            Assert.AreEqual((exceed ? DeviceEnrollmentErrorCode.ProfileDataTooLarge : DeviceEnrollmentErrorCode.CodeProofInvalid).ToString(),
                reply.ErrorCode);
            Assert.AreEqual(0, (await GetUserIdsAsync(target)).Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task TcpEnrollment_NoProgressChunksAreRejectedWithoutSpendingAnAttempt(bool afterMetadata)
    {
        await using var source = await ProductionEnrollmentTestHost.CreateAsync();
        await using var target = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(EnrollmentStepTimeout);
        var ct = deadline.Token;
        var code = await target.Endpoints.StartDeviceEnrollmentAsync(ct);
        var parsed = DeviceEnrollmentCode.Parse(code.Code);
        try
        {
            await using var listener = await LoopbackSyncListener.StartAsync(target.Services, ct);
            for (var i = 0; i < SyncConstants.MaxEnrollmentValidationAttempts; i++)
            {
                await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, target.Identity, source.Identity, ct);
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentStart,
                    new CompleteDeviceEnrollmentStartRequest { SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion }, ct);
                if (afterMetadata)
                {
                    await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk,
                        new CompleteDeviceEnrollmentChunk { SessionId = parsed.SessionId }, ct);
                }
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk,
                    new CompleteDeviceEnrollmentChunk(), ct);

                // Require the server to reject before the peer sends an end frame or closes.
                var frame = await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentReply, ct);
                var reply = frame.Parse(CompleteDeviceEnrollmentReply.Parser);
                Assert.IsFalse(reply.Ok);
                Assert.AreEqual(DeviceEnrollmentErrorCode.ProfileDataInvalid.ToString(), reply.ErrorCode);
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
            }

            var accepted = await CreateEnrollmentTcpClient(source).GetDeviceEnrollmentInfoAsync(
                listener.Endpoint.Address.ToString(), listener.Endpoint.Port, target.Identity.FingerprintHex,
                BuildEnrollmentInfoRequest(parsed), ct);
            Assert.IsTrue(accepted.Ok);
            Assert.AreEqual(0, (await GetUserIdsAsync(target)).Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    private static async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> BuildUnprovenEnrollmentAsync(
        ProductionEnrollmentTestHost source, ProductionEnrollmentTestHost target, string sessionId)
    {
        yield return BuildUnprovenEnrollmentChunk(source, target, sessionId, ByteString.CopyFrom(new byte[1]));
        await Task.CompletedTask;
    }

    private static CompleteDeviceEnrollmentChunk BuildUnprovenEnrollmentChunk(
        ProductionEnrollmentTestHost source, ProductionEnrollmentTestHost target, string sessionId, ByteString data) => new()
    {
        SessionId = sessionId,
        CodeProof = ByteString.CopyFrom(new byte[32]),
        SourceDeviceId = source.Identity.LocalDeviceId.ToString("N"),
        SourceOriginInstanceId = source.Identity.OriginInstanceId.ToString("N"),
        SourceSignPub = ByteString.CopyFrom(source.Identity.SignPublicKey),
        SourceTlsCertFingerprint = source.Identity.FingerprintHex,
        TargetDeviceId = target.Identity.LocalDeviceId.ToString("N"),
        TargetOriginInstanceId = target.Identity.OriginInstanceId.ToString("N"),
        SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion,
        SnapshotChunk = data
    };

    private static async Task AssertPinRejectedAsync(Func<Task> attempt)
    {
        try
        {
            await attempt();
            Assert.Fail("The connection accepted a certificate pin outside the operation's contract.");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
        }
    }

    private static async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> NoEnrollmentChunksAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static TcpSyncClientService CreateEnrollmentTcpClient(ProductionEnrollmentTestHost host) =>
        new(host.Identity, host.Services.GetRequiredService<ISyncDeviceIdentityService>(),
            host.Services.GetRequiredService<IDevicePresenceRegistry>(), host.Services.GetRequiredService<IServiceScopeFactory>());

    private static GetDeviceEnrollmentInfoRequest BuildEnrollmentInfoRequest(DeviceEnrollmentParsedCode parsed) => new()
    {
        SessionId = parsed.SessionId,
        CodeProof = ByteString.CopyFrom(DeviceEnrollmentCode.BuildEnrollmentInfoProof(parsed.SessionId, parsed.Secret)),
        SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
    };

    private static async Task ReplyWithEnrollmentFingerprintAsync(TcpListener listener, ProductionEnrollmentTestHost source,
        ProductionEnrollmentTestHost target, string fingerprint, CancellationToken ct)
    {
        await using var peer = await LoopbackSyncPeer.AcceptAsync(listener, target.Identity, source.Identity, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.GetDeviceEnrollmentInfoRequest, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.GetDeviceEnrollmentInfoReply, new GetDeviceEnrollmentInfoReply
        {
            Ok = true,
            DeviceId = target.Identity.LocalDeviceId.ToString("N"),
            OriginInstanceId = target.Identity.OriginInstanceId.ToString("N"),
            TlsCertFingerprint = fingerprint,
            SignPub = ByteString.CopyFrom(target.Identity.SignPublicKey),
            AgreementPub = ByteString.CopyFrom(target.Identity.AgreementPublicKey),
            DeviceType = (uint)target.Identity.DeviceType
        }, ct);
    }
}
