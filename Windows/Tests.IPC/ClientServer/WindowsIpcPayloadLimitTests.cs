using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Text;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcPayloadLimitTests
{
    private readonly WindowsIpcSerializer _serializer = new();
    private readonly WindowsIpcContractValidator _validator = new();

    [TestMethod]
    public void MaximumRequestPayloadSerializesWithinOuterFrameLimit()
    {
        var request = new IpcRequestEnvelope(
            long.MaxValue,
            IpcOperationId.SetBackgroundSyncEnabled,
            new byte[IpcContractLimits.MaximumInnerPayloadSize]);

        _validator.Validate(request);
        var serialized = _serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        _validator.ValidateSerializedRequestEnvelope(request.CorrelationId, serialized);

        Assert.IsTrue(serialized.Length <= WindowsIpcProtocol.MaximumPayloadSize);
    }

    [TestMethod]
    public void MaximumResponseResultSerializesWithinOuterFrameLimit()
    {
        var response = IpcResponseEnvelope.Success(
            long.MaxValue,
            new byte[IpcContractLimits.MaximumInnerPayloadSize]);

        _validator.Validate(response);
        var serialized = _serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
        _validator.ValidateSerializedResponseEnvelope(response.CorrelationId, serialized);

        Assert.IsTrue(serialized.Length <= WindowsIpcProtocol.MaximumPayloadSize);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OversizedRequestIsRejectedBeforeTransportWrite()
    {
        var connection = new RecordingWindowsIpcConnection(_serializer);
        var client = CreateClient(connection);
        await client.HandshakeAsync();
        var payload = CreateOversizedPayload("sensitive-request-content");

        var exception = await Assert.ThrowsAsync<IpcPayloadLimitExceededException>(async () =>
            await client.SendAsync(IpcOperationId.Ping, payload));

        Assert.AreEqual(IpcErrorCode.RequestPayloadTooLarge, exception.ErrorCode);
        Assert.AreEqual(
            "The IPC request payload exceeds the permitted size.",
            exception.SafeMessage);
        Assert.AreEqual(IpcErrorCategory.Validation, exception.ErrorCategory);
        Assert.IsTrue(exception.CorrelationId > 0);
        Assert.IsFalse(exception.IsRetryable);
        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.IsFalse(exception.SafeMessage.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.AreEqual(
            0,
            connection.StartedWrites.Count(frame => frame.Header.MessageKind == IpcMessageKind.Request));

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var followUpRequest = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(followUpRequest.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OversizedServerResultBecomesStructuredSafeError()
    {
        var oversizedResult = CreateOversizedPayload("sensitive-response-content");
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) => Task.FromResult(
                IpcResponseEnvelope.Success(
                    context.Request.CorrelationId,
                    oversizedResult)));
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping));

        Assert.AreEqual(IpcErrorCode.ResponsePayloadTooLarge, exception.Error.ErrorCode);
        Assert.AreEqual(
            "The IPC response result exceeds the permitted size.",
            exception.Error.SafeMessage);
        Assert.AreEqual(IpcErrorCategory.Validation, exception.Error.ErrorCategory);
        Assert.IsTrue(exception.Error.CorrelationId > 0);
        Assert.IsFalse(exception.Error.IsRetryable);
        Assert.IsFalse(exception.Error.RequiresProcessRestart);
        Assert.IsFalse(exception.Error.SafeMessage.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DeserializedOversizedRequestPayloadReturnsStableError()
    {
        var pair = new InMemoryIpcConnectionPair();
        var server = new WindowsIpcServerConnectionSession(
            pair.Server,
            _serializer,
            new WindowsIpcRequestDispatcher(
                new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() },
                _validator),
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.TestClient },
                IpcCapabilities.Control),
            contractValidator: _validator);
        var serverTask = server.RunAsync();
        await CompleteRawHandshakeAsync(pair);
        var request = new IpcRequestEnvelope(
            80,
            IpcOperationId.Ping,
            CreateOversizedPayload("sensitive-request-content"));
        var serialized = _serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        Assert.IsTrue(serialized.Length <= WindowsIpcProtocol.MaximumPayloadSize);

        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 80, serialized));
        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = _serializer.Deserialize(
            responseFrame.Payload,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);

        Assert.AreEqual(80, response.CorrelationId);
        Assert.AreEqual(80, response.Error?.CorrelationId);
        Assert.AreEqual(IpcErrorCode.RequestPayloadTooLarge, response.Error?.ErrorCode);
        Assert.AreEqual(
            "The IPC request payload exceeds the permitted size.",
            response.Error?.SafeMessage);
        Assert.AreEqual(IpcErrorCategory.Validation, response.Error?.ErrorCategory);
        Assert.IsFalse(response.Error!.IsRetryable);
        Assert.IsFalse(response.Error.RequiresProcessRestart);
        Assert.IsFalse(response.Error.SafeMessage.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        await pair.Client.DisposeAsync();
        await serverTask;
        await server.DisposeAsync();
    }

    [TestMethod]
    public void FinalSerializedEnvelopeSizeIsValidatedExplicitly()
    {
        var oversizedEnvelope = new byte[WindowsIpcProtocol.MaximumPayloadSize + 1];

        var requestException = Assert.ThrowsExactly<IpcPayloadLimitExceededException>(() =>
            _validator.ValidateSerializedRequestEnvelope(90, oversizedEnvelope));
        var responseException = Assert.ThrowsExactly<IpcPayloadLimitExceededException>(() =>
            _validator.ValidateSerializedResponseEnvelope(91, oversizedEnvelope));

        Assert.AreEqual(IpcErrorCode.SerializedEnvelopeTooLarge, requestException.ErrorCode);
        Assert.AreEqual(IpcErrorCode.SerializedEnvelopeTooLarge, responseException.ErrorCode);
        Assert.AreEqual(90, requestException.CorrelationId);
        Assert.AreEqual(91, responseException.CorrelationId);
        Assert.AreEqual(
            "The serialized IPC request envelope exceeds the permitted frame size.",
            requestException.SafeMessage);
        Assert.AreEqual(
            "The serialized IPC response envelope exceeds the permitted frame size.",
            responseException.SafeMessage);
        Assert.IsFalse(requestException.IsRetryable);
        Assert.IsFalse(responseException.RequiresProcessRestart);
    }

    [TestMethod]
    public void PayloadLimitErrorsRequireStableValidationMetadata()
    {
        foreach (var errorCode in new[]
        {
            IpcErrorCode.RequestPayloadTooLarge,
            IpcErrorCode.ResponsePayloadTooLarge,
            IpcErrorCode.SerializedEnvelopeTooLarge
        })
        {
            var valid = new IpcError(
                errorCode,
                IpcErrorCategory.Validation,
                "The IPC payload exceeds the permitted size.",
                99,
                DateTimeOffset.UtcNow,
                IsRetryable: false,
                RequiresProcessRestart: false);
            _validator.Validate(valid);

            var retryable = valid with { IsRetryable = true };
            Assert.ThrowsExactly<IpcPayloadException>(() =>
                _validator.Validate(retryable));
        }
    }

    [TestMethod]
    public void DeserializedInnerPayloadLimitIsEnforcedByContractValidation()
    {
        var request = new IpcRequestEnvelope(
            100,
            IpcOperationId.Ping,
            new byte[IpcContractLimits.MaximumInnerPayloadSize + 1]);
        var response = IpcResponseEnvelope.Success(
            101,
            new byte[IpcContractLimits.MaximumInnerPayloadSize + 1]);

        var requestException = Assert.ThrowsExactly<IpcPayloadLimitExceededException>(() =>
            _validator.Validate(request));
        var responseException = Assert.ThrowsExactly<IpcPayloadLimitExceededException>(() =>
            _validator.Validate(response));

        Assert.AreEqual(IpcErrorCode.RequestPayloadTooLarge, requestException.ErrorCode);
        Assert.AreEqual(IpcErrorCode.ResponsePayloadTooLarge, responseException.ErrorCode);
    }

    private WindowsIpcClient CreateClient(RecordingWindowsIpcConnection connection) =>
        new(
            connection,
            _serializer,
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control | IpcCapabilities.Status,
                Environment.ProcessId,
                0,
                Guid.NewGuid()));

    private async Task CompleteRawHandshakeAsync(InMemoryIpcConnectionPair pair)
    {
        var request = new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.TestClient,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control);
        var payload = _serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcHandshakeRequest);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.HandshakeRequest, 1, payload));
        var response = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(response);
        Assert.AreEqual(IpcMessageKind.HandshakeResponse, response.Header.MessageKind);
    }

    private static byte[] CreateOversizedPayload(string marker)
    {
        var payload = new byte[IpcContractLimits.MaximumInnerPayloadSize + 1];
        Encoding.UTF8.GetBytes(marker).CopyTo(payload, 0);
        return payload;
    }

    private static IpcFrame CreateFrame(
        IpcMessageKind messageKind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                messageKind,
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload);
}
