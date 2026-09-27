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
    public async Task DefinitelyNotSentMutationsRemainConclusiveForEveryMutationClassification()
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
        }
    }

    [TestMethod]
    public async Task SentMutationsWithoutResponseReportUnknownForEveryMutationClassification()
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
        }
    }

    [TestMethod]
    public async Task TransmissionUnknownMutationsReportUnknownForEveryMutationClassification()
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
    }

    [TestMethod]
    public async Task ConclusiveServerRejectionRemainsAuthoritativeForMutation()
    {
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.Conflict,
            EndpointRpcErrorCategory.Conflict,
            "The endpoint operation conflicts with current state.",
            1,
            DateTimeOffset.UtcNow,
            false,
            false,
            EndpointMutationOutcome.NotCommitted,
            Recovery: null);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(error)));
        await using var proxy = CreateProxy(transport);

        var exception = await Assert.ThrowsExactlyAsync<EndpointRpcRemoteException>(() =>
            proxy.SetLocalUserSyncOnAsync(EndpointRpcTestData.Token, true));
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, exception.Error.ErrorCode);
        Assert.HasCount(1, transport.Operations);
    }

    [TestMethod]
    public async Task OutcomeUnknownExceptionExposesIndependentRestartRequirement()
    {
        foreach (var requiresProcessRestart in new[] { false, true })
        {
            var error = new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                requiresProcessRestart,
                EndpointMutationOutcome.OutcomeUnknown,
                Recovery: null);
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
    public void RepresentativeOperationsCoverEveryCancellationClassification()
    {
        var representatives = new[]
        {
            EndpointOperationId.GetLocalDeviceInfo,
            EndpointOperationId.SetLocalUserSyncOn,
            EndpointOperationId.StartDeviceEnrollment,
            EndpointOperationId.Logout
        };

        CollectionAssert.AreEquivalent(
            Enum.GetValues<EndpointOperationCancellationClassification>().Cast<object>().ToArray(),
            representatives
                .Select(id => (object)EndpointOperationManifest.Get(id).CancellationClassification)
                .ToArray());
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
