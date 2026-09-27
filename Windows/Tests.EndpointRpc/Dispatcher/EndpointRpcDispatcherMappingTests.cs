using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Dispatcher;

[TestClass]
public sealed class EndpointRpcDispatcherMappingTests
{
    [TestMethod]
    public async Task EveryOperationInvokesExactlyItsMappedEndpointMethod()
    {
        var endpoints = new ThrowingRecordingEndpoints();
        await using var dispatcher = CreateDispatcher(new FixedEndpointRpcEndpointAdapter(endpoints));
        long correlationId = 10;

        foreach (var descriptor in EndpointOperationManifest.All)
        {
            var payload = SerializeRequest(descriptor);
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            var context = CreateContext(descriptor.OperationId, ++correlationId);
            var result = await dispatcher.DispatchAsync(
                context,
                payload,
                cancellationSource.Token);

            Assert.IsFalse(result.IsSuccess, descriptor.OperationId.ToString());
            Assert.AreEqual(descriptor.MethodName, endpoints.LastMethodName, descriptor.OperationId.ToString());
            if (descriptor.CancellationClassification == EndpointOperationCancellationClassification.ReadOnlySafelyCancellable)
                Assert.IsTrue(endpoints.LastCancellationToken.IsCancellationRequested, descriptor.OperationId.ToString());
            else
                Assert.AreEqual(CancellationToken.None, endpoints.LastCancellationToken, descriptor.OperationId.ToString());
        }
    }

    [TestMethod]
    public async Task EndpointAdapterReceivesTheClassifiedOperationCancellationToken()
    {
        var criticalAdapter = new ExceptionEndpointAdapter(new Exception("critical"));
        var readOnlyAdapter = new ExceptionEndpointAdapter(new Exception("read"));
        var criticalDescriptor = EndpointOperationManifest.Get(EndpointOperationId.Logout);
        var readOnlyDescriptor = EndpointOperationManifest.Get(EndpointOperationId.GetLocalDeviceInfo);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await using var criticalDispatcher = CreateDispatcher(criticalAdapter);
        await using var readOnlyDispatcher = CreateDispatcher(readOnlyAdapter);
        await criticalDispatcher.DispatchAsync(
            CreateContext(criticalDescriptor.OperationId, 21),
            SerializeRequest(criticalDescriptor),
            cancellationSource.Token);
        await readOnlyDispatcher.DispatchAsync(
            CreateContext(readOnlyDescriptor.OperationId, 22),
            SerializeRequest(readOnlyDescriptor),
            cancellationSource.Token);

        Assert.AreEqual(CancellationToken.None, criticalAdapter.LastContext!.CancellationToken);
        Assert.IsTrue(readOnlyAdapter.LastContext!.CancellationToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task InvalidRequestNeverInvokesEndpoints()
    {
        var endpoints = new ThrowingRecordingEndpoints();
        await using var dispatcher = CreateDispatcher(new FixedEndpointRpcEndpointAdapter(endpoints));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.GetSavedPasswordsEndpointRequest(),
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest);

        var result = await dispatcher.DispatchAsync(
            CreateContext(EndpointOperationId.GetSavedPasswords, 22),
            payload,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, result.Error!.ErrorCode);
        Assert.IsNull(endpoints.LastMethodName);
    }

    [TestMethod]
    public async Task WrongRequestTypeAndUnknownOperationAreRejected()
    {
        var endpoints = new ThrowingRecordingEndpoints();
        await using var dispatcher = CreateDispatcher(new FixedEndpointRpcEndpointAdapter(endpoints));
        var loginPayload = SerializeRequest(EndpointOperationManifest.Get(EndpointOperationId.Login));

        var wrongType = await dispatcher.DispatchAsync(
            CreateContext(EndpointOperationId.Register, 23),
            loginPayload,
            CancellationToken.None);
        var unknown = await dispatcher.DispatchAsync(
            CreateContext((EndpointOperationId)9999, 24),
            [],
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, wrongType.Error!.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCode.UnknownOperation, unknown.Error!.ErrorCode);
        Assert.IsNull(endpoints.LastMethodName);
    }

    [TestMethod]
    public async Task KnownAndUnknownBackendFailuresMapWithoutRawMessages()
    {
        const string secret = "database-path-and-secret-token";
        await using var known = CreateDispatcher(new ExceptionEndpointAdapter(new UnauthorizedAccessException(secret)));
        await using var unknown = CreateDispatcher(new ExceptionEndpointAdapter(new Exception(secret)));
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.GetLocalDeviceInfo);
        var payload = SerializeRequest(descriptor);

        var knownResult = await known.DispatchAsync(CreateContext(descriptor.OperationId, 25), payload, CancellationToken.None);
        var unknownResult = await unknown.DispatchAsync(CreateContext(descriptor.OperationId, 26), payload, CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.AuthorizationFailed, knownResult.Error!.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCode.BackendFailure, unknownResult.Error!.ErrorCode);
        Assert.IsFalse(knownResult.Error.SafeMessage.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(unknownResult.Error.SafeMessage.Contains(secret, StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task OperationSpecificRequestLimitRejectsBeforeEndpointInvocation()
    {
        var endpoints = new ThrowingRecordingEndpoints();
        await using var dispatcher = CreateDispatcher(new FixedEndpointRpcEndpointAdapter(endpoints));
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.GetLocalDeviceInfo);
        var payload = new byte[descriptor.MaximumRequestPayloadSize + 1];

        var result = await dispatcher.DispatchAsync(
            CreateContext(descriptor.OperationId, 27),
            payload,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(EndpointRpcErrorCode.RequestPayloadTooLarge, result.Error!.ErrorCode);
        Assert.IsNull(endpoints.LastMethodName);
    }

    [TestMethod]
    public async Task LoginAuthenticationFailureUsesNonEnumeratingSafeError()
    {
        const string secret = "unknown-user-and-password";
        await using var dispatcher = CreateDispatcher(new ExceptionEndpointAdapter(
            new UserNotFoundException(secret)));
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.Login);
        var payload = SerializeRequest(descriptor);

        var result = await dispatcher.DispatchAsync(
            CreateContext(descriptor.OperationId, 28),
            payload,
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.AuthenticationFailed, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Authentication, result.Error.ErrorCategory);
        Assert.IsFalse(result.Error.SafeMessage.Contains(secret, StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task ValidResponseAboveInlineLimitCreatesBoundedLargeTransfer()
    {
        await using var dispatcher = CreateDispatcher(new FixedEndpointRpcEndpointAdapter(
            new OversizedSavedPasswordsEndpoints()));
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.GetSavedPasswords);
        var payload = SerializeRequest(descriptor);
        var context = CreateContext(descriptor.OperationId, 29);

        var result = await dispatcher.DispatchAsync(
            context,
            payload,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.IsLargeResult);
        Assert.IsNotNull(result.LargeResult);
        Assert.IsTrue(result.LargeResult.DeclaredTotalLength > descriptor.MaximumResponsePayloadSize);
        Assert.IsTrue(result.LargeResult.DeclaredTotalLength <= descriptor.MaximumLogicalResponsePayloadSize);
        Assert.IsTrue(result.LargeResult.DeclaredChunkCount <= EndpointRpcLimits.MaximumLargeResultChunkCount);
        Assert.AreEqual(1, dispatcher.LargeResultTransferStore.Count);
        Assert.IsTrue(dispatcher.LargeResultTransferStore.Release(
            context.ConnectionId,
            context.PeerSessionId,
            new PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.ReleaseEndpointLargeResultRequest
            {
                TransferId = result.LargeResult.TransferId,
                OriginalCorrelationId = result.LargeResult.OriginalCorrelationId
            }));
    }

    [TestMethod]
    public async Task ImpossibleReadOnlyResultAboveLargeTransferLimitIsConclusive()
    {
        var serializer = new EndpointRpcSerializer(
            serializationObserver: null,
            serializedPayloadOverride: type =>
                type == typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses.GetSavedPasswordsEndpointResponse)
                    ? new byte[EndpointRpcLimits.MaximumLargeResultTotalBytes + 1]
                    : null);
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(new SuccessfulRecordingEndpoints()),
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper());
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.GetSavedPasswords);

        var result = await dispatcher.DispatchAsync(
            CreateContext(descriptor.OperationId, 31),
            SerializeRequest(descriptor),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.ResponsePayloadTooLarge, result.Error!.ErrorCode);
        Assert.AreNotEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error.ErrorCode);
        Assert.AreEqual(0, dispatcher.LargeResultTransferStore.Count);
    }


    [TestMethod]
    public void SyncRouteDisabledMapsToOperationRejectedBeforeUnauthorizedBaseType()
    {
        var mapper = new EndpointRpcBackendErrorMapper();
        var context = CreateContext(EndpointOperationId.GetSavedPasswords, 30);

        var error = mapper.Map(
            new SyncRouteDisabledException("route-secret"),
            context);

        Assert.AreEqual(EndpointRpcErrorCode.OperationRejected, error.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Validation, error.ErrorCategory);
        Assert.IsFalse(error.SafeMessage.Contains("route-secret", StringComparison.Ordinal));
    }


    [TestMethod]
    public void EnrollmentAvailabilityAndRestartFailuresMapTruthfully()
    {
        var mapper = new EndpointRpcBackendErrorMapper();
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 30);

        var availability = mapper.Map(
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.LocalNetworkUnavailable,
                "network-secret"),
            context);
        var restart = mapper.Map(
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                "database-secret"),
            context);

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, availability.ErrorCode);
        Assert.IsTrue(availability.IsRetryable);
        Assert.IsFalse(availability.RequiresProcessRestart);
        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, restart.ErrorCode);
        Assert.IsFalse(restart.IsRetryable);
        Assert.IsTrue(restart.RequiresProcessRestart);
        Assert.IsFalse(availability.SafeMessage.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(restart.SafeMessage.Contains("secret", StringComparison.Ordinal));
    }

    private static EndpointRpcDispatcher CreateDispatcher(IEndpointRpcEndpointAdapter adapter) =>
        new(
            adapter,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper());

    private static EndpointRequestContext CreateContext(EndpointOperationId operationId, long correlationId) =>
        new(
            Guid.NewGuid(),
            correlationId,
            operationId,
            IpcPeerRole.Ui,
            1234,
            Guid.NewGuid(),
            CancellationToken.None);

    private static byte[] SerializeRequest(EndpointOperationDescriptor descriptor)
    {
        var value = EndpointRpcTestData.CreateRequest(descriptor.OperationId);
        var typeInfo = EndpointRpcJsonContext.Default.GetTypeInfo(descriptor.RequestType)
            ?? throw new InvalidOperationException(descriptor.RequestType.FullName);
        return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    }
}
