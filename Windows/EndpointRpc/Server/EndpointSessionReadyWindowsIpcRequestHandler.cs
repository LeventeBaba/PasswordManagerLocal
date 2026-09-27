using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointSessionReadyWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IEndpointRpcSessionReadiness _readiness;
    private readonly IEndpointRpcAdmissionPolicy _admissionPolicy;

    public EndpointSessionReadyWindowsIpcRequestHandler(
        IEndpointRpcSessionReadiness readiness,
        IEndpointRpcAdmissionPolicy admissionPolicy)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _admissionPolicy = admissionPolicy
            ?? throw new ArgumentNullException(nameof(admissionPolicy));
    }

    public IpcOperationId OperationId => IpcOperationId.EndpointSessionReady;

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.EnsureNoPayload();
        if (!_admissionPolicy.TryEnterRequest(out var admissionLease))
        {
            return Task.FromResult(IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.AgentUnavailable,
                    IpcErrorCategory.Availability,
                    "The Windows agent endpoint channel is unavailable.",
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false)));
        }

        using (admissionLease)
        {
            return Task.FromResult(context.Success(
                new RequestAcceptedDto(_readiness.IsReady(context.Connection)),
                WindowsIpcJsonContext.Default.RequestAcceptedDto));
        }
    }
}
