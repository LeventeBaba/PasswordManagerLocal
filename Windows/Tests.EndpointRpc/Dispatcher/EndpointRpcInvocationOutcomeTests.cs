using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Dispatcher;

[TestClass]
public sealed class EndpointRpcInvocationOutcomeTests
{
    [TestMethod]
    public async Task MalformedAndInvalidRequestsFailBeforeInvocation()
    {
        var endpoints = new ConfiguredLogoutEndpoints();
        var malformedContext = CreateContext(EndpointOperationId.Logout, 101);
        var invalidContext = CreateContext(EndpointOperationId.Logout, 102);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());
        var invalidPayload = JsonSerializer.SerializeToUtf8Bytes(
            new PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.LogoutEndpointRequest(),
            EndpointRpcJsonContext.Default.LogoutEndpointRequest);

        var malformed = await dispatcher.DispatchAsync(
            malformedContext,
            "{"u8.ToArray(),
            CancellationToken.None);
        var invalid = await dispatcher.DispatchAsync(
            invalidContext,
            invalidPayload,
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, malformed.Error!.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, invalid.Error!.ErrorCode);
        Assert.AreEqual(0, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.BeforeInvocation, malformedContext.Invocation.Stage);
        Assert.AreEqual(EndpointInvocationStage.BeforeInvocation, invalidContext.Invocation.Stage);
    }

    [TestMethod]
    public async Task UnknownMutationFailureAfterInvocationIsOutcomeUnknown()
    {
        var endpoints = new ConfiguredLogoutEndpoints(new IOException("secret failure"));
        var context = CreateContext(EndpointOperationId.Logout, 103);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.Invoking, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsFalse(result.Error.SafeMessage.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(result.Error.IsRetryable);
        Assert.IsFalse(result.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task DatabaseVersionFailureAfterMutationInvocationIsOutcomeUnknownAndRequiresRestart()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.Logout,
            new DatabaseVersionNotSupportedException(1, 2, 3));
        var context = CreateContext(EndpointOperationId.Logout, 111);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.Invoking, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task UnknownMutationOutcomePreservesNestedRestartRequirement()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.Logout,
            new IOException(
                "outer runtime wrapper",
                new AggregateException(
                    new InvalidOperationException("ordinary failure"),
                    new DatabaseVersionNotSupportedException(1, 2, 3))));
        var context = CreateContext(EndpointOperationId.Logout, 124);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentUnsupportedDatabaseFailureWithoutCommitContextIsConclusiveAndRequiresRestart()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                "unsupported database"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 112);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.Invoking, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task PreInvocationDatabaseVersionFailureRemainsConclusiveAndRequiresRestart()
    {
        var serializer = new EndpointRpcSerializer();
        var restartHandler = new RecordingEndpointRpcRestartRequirementHandler();
        await using var dispatcher = new EndpointRpcDispatcher(
            new ExceptionEndpointAdapter(
                new DatabaseVersionNotSupportedException(1, 2, 3)),
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper(),
            restartHandler);
        var context = CreateContext(EndpointOperationId.Logout, 113);

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(EndpointInvocationStage.BeforeInvocation, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, result.Error!.ErrorCode);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
        Assert.AreEqual(1, restartHandler.RequestCount);
        Assert.IsInstanceOfType<DatabaseVersionNotSupportedException>(restartHandler.Failure);
    }

    [TestMethod]
    public async Task ReadOnlyDatabaseVersionFailureRemainsConclusiveAndRequiresRestart()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.GetLocalDeviceInfo,
            new DatabaseVersionNotSupportedException(1, 2, 3));
        var context = CreateContext(EndpointOperationId.GetLocalDeviceInfo, 114);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.GetLocalDeviceInfo),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.Invoking, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, result.Error!.ErrorCode);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task KnownPreCommitMutationRejectionRemainsConclusive()
    {
        var endpoints = new ConfiguredLogoutEndpoints(new InvalidInputException(["invalid"]));
        var context = CreateContext(EndpointOperationId.Logout, 104);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.Invoking, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
    }

    [TestMethod]
    public async Task MutationResponseValidationFailureIsOutcomeUnknown()
    {
        var endpoints = new InvalidRegisterResponseEndpoints();
        var context = CreateContext(EndpointOperationId.Register, 105);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Register),
            CancellationToken.None);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(EndpointInvocationStage.InvocationCompleted, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
    }

    [TestMethod]
    public async Task MutationResponseSerializationFailureIsOutcomeUnknown()
    {
        var endpoints = new ConfiguredLogoutEndpoints();
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(LogoutEndpointResponse))
                throw new EndpointRpcPayloadException("response serialization failed");
        });
        var context = CreateContext(EndpointOperationId.Logout, 106);
        await using var dispatcher = CreateDispatcher(endpoints, serializer);
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(EndpointInvocationStage.ResponseValidated, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
    }

    [TestMethod]
    public async Task MutationResponseSizeFailureIsOutcomeUnknown()
    {
        var endpoints = new ConfiguredLogoutEndpoints();
        var serializer = new EndpointRpcSerializer(
            serializationObserver: null,
            serializedPayloadOverride: type => type == typeof(LogoutEndpointResponse)
                ? new byte[EndpointRpcLimits.MaximumSmallResponsePayloadSize + 1]
                : null);
        var context = CreateContext(EndpointOperationId.Logout, 107);
        await using var dispatcher = CreateDispatcher(endpoints, serializer);
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(EndpointInvocationStage.ResponseSerialized, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
    }

    [TestMethod]
    public async Task ReadOnlyResponseSerializationFailureRemainsOrdinaryFailure()
    {
        var endpoints = new SuccessfulRecordingEndpoints();
        var serializer = new EndpointRpcSerializer(type =>
        {
            if (type == typeof(GetLocalDeviceInfoEndpointResponse))
                throw new EndpointRpcPayloadException("response serialization failed");
        });
        var context = CreateContext(EndpointOperationId.GetLocalDeviceInfo, 108);
        await using var dispatcher = CreateDispatcher(endpoints, serializer);
        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.GetLocalDeviceInfo),
            CancellationToken.None);

        Assert.AreEqual(EndpointInvocationStage.ResponseValidated, context.Invocation.Stage);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, result.Error!.ErrorCode);
        Assert.AreNotEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error.ErrorCode);
    }

    [TestMethod]
    public async Task InvocationStagesAdvanceMonotonicallyThroughSuccessfulResponseSerialization()
    {
        var context = CreateContext(EndpointOperationId.Logout, 109);
        await using var dispatcher = CreateDispatcher(
            new ConfiguredLogoutEndpoints(),
            new EndpointRpcSerializer());
        var result = await dispatcher.DispatchAsync(
                context,
                SerializeRequest(EndpointOperationId.Logout),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(EndpointInvocationStage.ResponseSerialized, context.Invocation.Stage);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.Invocation.MarkInvocationCompleted());
    }

    [TestMethod]
    public async Task OutcomeUnknownEncodingFailureDoesNotBecomeConclusiveEndpointFailure()
    {
        var endpoints = new ConfiguredLogoutEndpoints(new IOException("backend failure"));
        var dispatcherSerializer = new EndpointRpcSerializer();
        await using var dispatcher = CreateDispatcher(endpoints, dispatcherSerializer);
        var failingSerializer = new EndpointRpcSerializer(
            serializationObserver: null,
            serializedPayloadOverride: type => type == typeof(EndpointRpcError)
                ? new byte[EndpointRpcLimits.MaximumErrorPayloadSize + 1]
                : null);
        var codec = new EndpointRpcMessageCodec(failingSerializer);
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            new EndpointRpcContractValidator(),
            failingSerializer,
            dispatcher.LargeResultTransferStore,
            new EndpointRpcBackendErrorMapper(),
            new ControllableEndpointRpcAdmissionPolicy());
        var connection = new IpcConnectionContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            1234,
            0,
            Guid.NewGuid(),
            IpcCapabilities.EndpointRpc);
        var requestPayload = SerializeRequest(EndpointOperationId.Logout);
        var encodedRequest = codec.EncodeRequest(EndpointOperationId.Logout, requestPayload);
        var requestContext = new IpcRequestContext(
            connection,
            new IpcRequestEnvelope(110, IpcOperationId.EndpointRpcRequest, encodedRequest),
            new WindowsIpcSerializer());

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            handler.HandleAsync(requestContext, CancellationToken.None));

        Assert.AreEqual(1, endpoints.InvocationCount);
        Array.Clear(requestPayload);
    }



    [TestMethod]
    public async Task EnrollmentFullSuccessReturnsTheExistingSuccessResponse()
    {
        var endpoints = new SuccessfulRecordingEndpoints();
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 119);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Error);
        Assert.AreEqual(EndpointInvocationStage.ResponseSerialized, context.Invocation.Stage);
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.AddDeviceByCodeAsync));
    }

    [TestMethod]
    public async Task EnrollmentInvalidCodeBeforeCommitIsConclusive()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(DeviceEnrollmentErrorCode.InvalidCode, "invalid code"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 120);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationRejected, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentTargetMissingBeforeCommitIsConclusive()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceNotFound, "target missing"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 121);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.NotFound, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentIdentityConflictBeforeCommitIsConclusive()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.DeviceIdentityConflict,
                "identity conflict"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 115);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.Conflict, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
        Assert.IsFalse(result.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task EnrollmentIdentityConflictAfterCommitRequiresRecovery()
    {
        var recovery = CreatePartialCommitFailure(
            DeviceEnrollmentErrorCode.DeviceIdentityConflict,
            requiresProcessRestart: false);
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            recovery);
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 116);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Recovery, result.Error.ErrorCategory);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, result.Error.MutationOutcome);
        Assert.IsFalse(result.Error.IsRetryable);
        Assert.IsFalse(result.Error.RequiresProcessRestart);
        Assert.IsNotNull(result.Error.Recovery);
        Assert.AreEqual(recovery.TargetDeviceId, result.Error.Recovery.TargetDeviceId);
        Assert.AreEqual(recovery.TargetOriginInstanceId, result.Error.Recovery.TargetOriginInstanceId);
        Assert.AreEqual(recovery.EnrollmentCommitId, result.Error.Recovery.EnrollmentCommitId);
        Assert.IsTrue(result.Error.Recovery.RecoveryAvailable);
        Assert.IsTrue(result.Error.Recovery.TransferPending);
        Assert.IsTrue(result.Error.Recovery.RequiresSignedRemovalToUndo);
    }

    [TestMethod]
    public async Task EnrollmentUnsupportedDatabaseAfterCommitRequiresRecoveryAndRestart()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            CreatePartialCommitFailure(
                DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
                requiresProcessRestart: true));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 117);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, result.Error.MutationOutcome);
        Assert.IsTrue(result.Error.RequiresProcessRestart);
        Assert.IsNotNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentKnownRollbackWithUnclassifiedCauseIsConclusive()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.Unknown,
                "rolled back",
                isKnownNotCommitted: true));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 122);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.BackendFailure, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentUnknownDomainFailureAfterInvocationRemainsOutcomeUnknown()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new DeviceEnrollmentException(DeviceEnrollmentErrorCode.Unknown, "uncertain transaction"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 119);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task KnownGenericPartialCommitUsesManifestPolicyWithoutEnrollmentMetadata()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.ChangeMasterPassword,
            new MutationPartiallyCommittedException("committed but local session refresh failed"));
        var context = CreateContext(EndpointOperationId.ChangeMasterPassword, 120);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.ChangeMasterPassword),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
        Assert.IsFalse(result.Error.IsRetryable);
    }

    [TestMethod]
    public async Task GenericPartialCommitCannotMasqueradeAsEnrollmentRecovery()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new MutationPartiallyCommittedException("missing immutable enrollment recovery facts"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 122);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task EnrollmentPartialCommitCannotBeBoundToAnotherOperation()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.ChangeMasterPassword,
            CreatePartialCommitFailure(DeviceEnrollmentErrorCode.DeviceIdentityConflict, false));
        var context = CreateContext(EndpointOperationId.ChangeMasterPassword, 123);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.ChangeMasterPassword),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
    }

    [TestMethod]
    public async Task PartialCommitAssertionForNonPartialPolicyIsOutcomeUnknown()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.Logout,
            new MutationPartiallyCommittedException("unexpected partial assertion"));
        var context = CreateContext(EndpointOperationId.Logout, 121);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.Logout),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
    }

    [TestMethod]
    public void RepresentativeMutationPoliciesClassifyKnownPartialCommitAssertions()
    {
        var mapper = new EndpointRpcBackendErrorMapper();
        var knownPartialOperations = new[]
        {
            EndpointOperationId.DeleteUserAccount,
            EndpointOperationId.ChangeMasterPassword,
            EndpointOperationId.InitializeRememberMeSession,
            EndpointOperationId.UpdatePassword,
            EndpointOperationId.ExportPasswordsToUser,
            EndpointOperationId.ExportCustomUserColorsToUser,
            EndpointOperationId.ExportPasswordTagsToUser
        };

        foreach (var operationId in knownPartialOperations)
        {
            var context = CreateContext(operationId, 200 + (long)operationId);
            context.Invocation.MarkInvoking();
            Assert.IsTrue(mapper.TryMapKnownMutationFailure(
                new MutationPartiallyCommittedException("authoritative state committed"),
                context,
                out var error), operationId.ToString());
            Assert.AreEqual(EndpointRpcErrorCode.OperationPartiallyCommitted, error.ErrorCode, operationId.ToString());
            Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, error.MutationOutcome, operationId.ToString());
            Assert.IsNull(error.Recovery, operationId.ToString());
            Assert.IsFalse(error.IsRetryable, operationId.ToString());
        }

        var atomicContext = CreateContext(EndpointOperationId.DisconnectUserDevice, 299);
        atomicContext.Invocation.MarkInvoking();
        Assert.IsFalse(mapper.TryMapKnownMutationFailure(
            new MutationPartiallyCommittedException("invalid partial assertion"),
            atomicContext,
            out _));
    }

    [TestMethod]
    public async Task EnrollmentUnknownFailureAfterInvocationRemainsOutcomeUnknown()
    {
        var endpoints = new ConfiguredEndpointFailureEndpoints(
            EndpointOperationId.AddDeviceByCode,
            new IOException("response lost"));
        var context = CreateContext(EndpointOperationId.AddDeviceByCode, 118);
        await using var dispatcher = CreateDispatcher(endpoints, new EndpointRpcSerializer());

        var result = await dispatcher.DispatchAsync(
            context,
            SerializeRequest(EndpointOperationId.AddDeviceByCode),
            CancellationToken.None);

        Assert.AreEqual(EndpointRpcErrorCode.OperationOutcomeUnknown, result.Error!.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.OutcomeUnknown, result.Error.MutationOutcome);
        Assert.IsNull(result.Error.Recovery);
        Assert.IsFalse(result.Error.IsRetryable);
    }

    private static EndpointRpcDispatcher CreateDispatcher(
        PasswordManagerLocal.Common.Contracts.Endpoints.IEndpoints endpoints,
        EndpointRpcSerializer serializer) =>
        new(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper());


    private static DeviceEnrollmentPartiallyCommittedException CreatePartialCommitFailure(
        DeviceEnrollmentErrorCode errorCode,
        bool requiresProcessRestart) =>
        new(
            errorCode,
            "post-commit enrollment failure",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            recoveryAvailable: true,
            transferPending: true,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart);

    private static EndpointRequestContext CreateContext(
        EndpointOperationId operationId,
        long correlationId) =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            correlationId,
            operationId,
            IpcPeerRole.Ui,
            1234,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            CancellationToken.None);

    private static byte[] SerializeRequest(EndpointOperationId operationId)
    {
        var descriptor = EndpointOperationManifest.Get(operationId);
        var value = EndpointRpcTestData.CreateRequest(operationId);
        var typeInfo = EndpointRpcJsonContext.Default.GetTypeInfo(descriptor.RequestType)
            ?? throw new InvalidOperationException(descriptor.RequestType.FullName);
        return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    }
}
