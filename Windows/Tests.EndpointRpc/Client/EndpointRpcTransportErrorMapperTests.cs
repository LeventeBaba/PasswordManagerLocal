using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Client;

[TestClass]
public sealed class EndpointRpcTransportErrorMapperTests
{
    [TestMethod]
    public void AuthorizationFailureUsesEndpointSafeMessageWithoutTransportDiagnostics()
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var source = new IpcError(
            IpcErrorCode.UnsupportedCapability,
            IpcErrorCategory.Protocol,
            "sensitive transport detail",
            occurredAtUtc,
            false,
            true);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.AuthorizationFailed, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointFailureKind.Generic, exception.Error.FailureKind);
        Assert.AreEqual("The endpoint connection is not authorized.", exception.Message);
        Assert.IsFalse(exception.Message.Contains("sensitive", StringComparison.Ordinal));
        Assert.IsFalse(exception.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public void RuntimeUnavailablePreservesProcessRestartRequirement()
    {
        var source = new IpcError(
            IpcErrorCode.AgentUnavailable,
            IpcErrorCategory.Availability,
            "transport detail",
            DateTimeOffset.UtcNow,
            true,
            true);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointFailureKind.Generic, exception.Error.FailureKind);
        Assert.IsTrue(exception.Error.RequiresProcessRestart);
    }

    [TestMethod]
    [DataRow(IpcErrorCode.RequestRejected, EndpointRpcErrorCode.OperationRejected)]
    [DataRow(IpcErrorCode.ResponsePayloadTooLarge, EndpointRpcErrorCode.ResponsePayloadTooLarge)]
    public void ValidationTransportFailureMapsToStableEndpointCode(IpcErrorCode sourceCode, EndpointRpcErrorCode expected)
    {
        var source = new IpcError(sourceCode, IpcErrorCategory.Validation, "transport detail",
            DateTimeOffset.UtcNow, false, false);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(expected, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointFailureKind.Generic, exception.Error.FailureKind);
    }

    [TestMethod]
    public void ConclusiveMutationTransportRejectionIsMarkedNotCommitted()
    {
        var source = new IpcError(
            IpcErrorCode.UnauthorizedOperation,
            IpcErrorCategory.Protocol,
            "transport detail",
            DateTimeOffset.UtcNow,
            false,
            false);

        var exception = EndpointRpcTransportErrorMapper.Map(
            source,
            isMutatingOperation: true);

        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, exception.Error.MutationOutcome);
        Assert.AreEqual(EndpointRpcErrorCode.AuthorizationFailed, exception.Error.ErrorCode);
    }

    [TestMethod]
    public void GenericPostHandlerFailuresAreMarkedAsTransmissionUncertainForMutations()
    {
        var postHandlerFailures = new[]
        {
            IpcErrorCode.HandlerFailed,
            IpcErrorCode.InvalidEnvelope,
            IpcErrorCode.InvalidPayload,
            IpcErrorCode.RequestCancelled,
            IpcErrorCode.InternalFailure,
            IpcErrorCode.ResponsePayloadTooLarge,
            IpcErrorCode.SerializedEnvelopeTooLarge
        };

        Assert.IsTrue(postHandlerFailures.All(WindowsEndpointRpcTransport.IsPostHandlerFailure));
        Assert.IsFalse(WindowsEndpointRpcTransport.IsPostHandlerFailure(IpcErrorCode.UnauthorizedOperation));
        Assert.IsFalse(WindowsEndpointRpcTransport.IsPostHandlerFailure(IpcErrorCode.RequestPayloadTooLarge));
        Assert.IsFalse(WindowsEndpointRpcTransport.IsPostHandlerFailure(IpcErrorCode.TooManyRequests));
    }

}
