using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Client;

[TestClass]
public sealed class EndpointRpcTransportErrorMapperTests
{
    [TestMethod]
    public void AuthorizationFailureUsesEndpointSafeMessageAndPreservesMetadata()
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var source = new IpcError(
            IpcErrorCode.UnsupportedCapability,
            IpcErrorCategory.Protocol,
            "sensitive transport detail",
            42,
            occurredAtUtc,
            false,
            true);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.AuthorizationFailed, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Authorization, exception.Error.ErrorCategory);
        Assert.AreEqual("The endpoint connection is not authorized.", exception.Message);
        Assert.AreEqual(42, exception.Error.CorrelationId);
        Assert.AreEqual(occurredAtUtc, exception.Error.OccurredAtUtc);
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
            8,
            DateTimeOffset.UtcNow,
            true,
            true);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.RuntimeUnavailable, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Availability, exception.Error.ErrorCategory);
        Assert.IsTrue(exception.Error.RequiresProcessRestart);
    }

    [TestMethod]
    public void RequestRejectedMapsToValidOperationRejectedCategory()
    {
        var source = new IpcError(
            IpcErrorCode.RequestRejected,
            IpcErrorCategory.Validation,
            "transport detail",
            9,
            DateTimeOffset.UtcNow,
            false,
            false);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.OperationRejected, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Validation, exception.Error.ErrorCategory);
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

    [TestMethod]
    public void OversizedResponseMapsToEndpointResponseLimitError()
    {
        var source = new IpcError(
            IpcErrorCode.ResponsePayloadTooLarge,
            IpcErrorCategory.Validation,
            "outer detail",
            7,
            DateTimeOffset.UtcNow,
            false,
            false);

        var exception = EndpointRpcTransportErrorMapper.Map(source);

        Assert.AreEqual(EndpointRpcErrorCode.ResponsePayloadTooLarge, exception.Error.ErrorCode);
        Assert.AreEqual(EndpointRpcErrorCategory.Validation, exception.Error.ErrorCategory);
    }
}
