using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Client;

[TestClass]
public sealed class EndpointRpcTransmissionMappingTests
{
    [TestMethod]
    public async Task DefinitelyNotSentMutationsRemainConclusiveForEveryCommitModel()
    {
        foreach (var operationId in MutationRepresentatives())
        {
            var sends = 0;
            var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromException<byte[]>(new IOException("not sent"));
            }, EndpointRpcTransmissionState.DefinitelyNotSent);
            await using var proxy = CreateProxy(transport);

            await Assert.ThrowsExactlyAsync<IOException>(() => InvokeAsync(proxy, operationId));
            Assert.AreEqual(1, sends, operationId.ToString());
            CollectionAssert.AreEqual(new[] { true }, transport.MutationFlags, operationId.ToString());
        }
    }

    [TestMethod]
    public async Task SentMutationsWithoutResponseReportUnknownForEveryCommitModel()
    {
        foreach (var operationId in MutationRepresentatives())
        {
            var sends = 0;
            var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromException<byte[]>(new IOException("response lost"));
            }, EndpointRpcTransmissionState.Sent);
            await using var proxy = CreateProxy(transport);

            await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
                InvokeAsync(proxy, operationId));
            Assert.AreEqual(1, sends, operationId.ToString());
            CollectionAssert.AreEqual(new[] { true }, transport.MutationFlags, operationId.ToString());
        }
    }

    [TestMethod]
    public async Task TransmissionUnknownMutationsReportUnknownForEveryCommitModel()
    {
        foreach (var operationId in MutationRepresentatives())
        {
            var sends = 0;
            var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromException<byte[]>(new IOException("send boundary failed"));
            }, EndpointRpcTransmissionState.TransmissionUnknown);
            await using var proxy = CreateProxy(transport);

            await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
                InvokeAsync(proxy, operationId));
            Assert.AreEqual(1, sends, operationId.ToString());
            CollectionAssert.AreEqual(new[] { true }, transport.MutationFlags, operationId.ToString());
        }
    }

    [TestMethod]
    public async Task ReadOnlyTransportFailureNeverUsesMutationOutcomeException()
    {
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new IOException("response lost")),
            EndpointRpcTransmissionState.Sent);
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<IOException>(() => proxy.GetLocalDeviceInfoAsync());
        Assert.HasCount(1, transport.Operations);
        CollectionAssert.AreEqual(new[] { false }, transport.MutationFlags);
    }

    [TestMethod]
    public async Task ConclusiveServerRejectionRemainsAuthoritativeForMutation()
    {
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.Conflict,
            EndpointFailureKind.Generic,
            "The endpoint operation conflicts with current state.",
            RequiresProcessRestart: false,
            EndpointMutationOutcome.NotCommitted);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(error)));
        await using var proxy = CreateProxy(transport);

        var exception = await Assert.ThrowsExactlyAsync<EndpointFailureException>(() =>
            proxy.SetLocalUserSyncOnAsync(EndpointRpcTestData.Token, true));
        Assert.AreEqual(EndpointFailureKind.Generic, exception.FailureKind);
        var remote = Assert.IsInstanceOfType<EndpointRpcRemoteException>(exception.InnerException);
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, remote.Error.ErrorCode);
        Assert.HasCount(1, transport.Operations);
    }

    [TestMethod]
    public async Task OutcomeUnknownExceptionExposesIndependentRestartRequirement()
    {
        foreach (var requiresProcessRestart in new[] { false, true })
        {
            var error = new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointFailureKind.Generic,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                requiresProcessRestart,
                EndpointMutationOutcome.OutcomeUnknown);
            var transport = new RecordingEndpointRpcTransport((_, _, _) =>
                Task.FromException<byte[]>(new EndpointRpcRemoteException(error)));
            await using var proxy = CreateProxy(transport);

            var exception = await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
                proxy.LogoutAsync(EndpointRpcTestData.Token));

            Assert.AreEqual(requiresProcessRestart, exception.RequiresProcessRestart);
            Assert.AreSame(error, exception.Error);
            Assert.AreEqual(EndpointOperationId.Logout, exception.OperationId);
        }
    }

    [TestMethod]
    public async Task ProxyTransportMutationFlagComesFromCommitModel()
    {
        var transport = new RecordingEndpointRpcTransport((operationId, _, _) =>
            Task.FromResult(operationId == EndpointOperationId.GetLocalUserSyncOn
                ? "{\"isSyncOn\":true}"u8.ToArray()
                : "{}"u8.ToArray()));
        await using var proxy = CreateProxy(transport);

        await proxy.GetLocalUserSyncOnAsync(EndpointRpcTestData.Token);
        await proxy.SetLocalUserSyncOnAsync(EndpointRpcTestData.Token, true);

        CollectionAssert.AreEqual(
            new[]
            {
                EndpointOperationManifest.Get(EndpointOperationId.GetLocalUserSyncOn).MutatesState,
                EndpointOperationManifest.Get(EndpointOperationId.SetLocalUserSyncOn).MutatesState
            },
            transport.MutationFlags);
        CollectionAssert.AreEqual(new[] { false, true }, transport.MutationFlags);
    }

    private static IReadOnlyList<EndpointOperationId> MutationRepresentatives() =>
    [
        EndpointOperationId.ChangeUsername,
        EndpointOperationId.SetLocalUserSyncOn,
        EndpointOperationId.AddDeviceByCode,
        EndpointOperationId.StartDeviceEnrollment,
        EndpointOperationId.Logout
    ];

    private static Task InvokeAsync(
        NamedPipeEndpointsProxy proxy,
        EndpointOperationId operationId) => operationId switch
    {
        EndpointOperationId.ChangeUsername =>
            proxy.ChangeUsernameAsync(EndpointRpcTestData.Token, "ValidUser2"),
        EndpointOperationId.SetLocalUserSyncOn =>
            proxy.SetLocalUserSyncOnAsync(EndpointRpcTestData.Token, true),
        EndpointOperationId.AddDeviceByCode =>
            proxy.AddDeviceByCodeAsync(EndpointRpcTestData.Token, "ABCD2345"),
        EndpointOperationId.StartDeviceEnrollment =>
            proxy.StartDeviceEnrollmentAsync(),
        EndpointOperationId.Logout =>
            proxy.LogoutAsync(EndpointRpcTestData.Token),
        _ => throw new ArgumentOutOfRangeException(nameof(operationId))
    };

    private static NamedPipeEndpointsProxy CreateProxy(IEndpointRpcTransport transport) =>
        new(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());
}
