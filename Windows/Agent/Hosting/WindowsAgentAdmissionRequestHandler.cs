using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Agent.Localization;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentAdmissionRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private readonly IWindowsIpcRequestHandler _inner;
    private readonly IAgentLocalizer _localizer;

    public WindowsAgentAdmissionRequestHandler(
        IWindowsAgentAdmissionGate admissionGate,
        IWindowsIpcRequestHandler inner,
        IAgentLocalizer localizer)
    {
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public IpcOperationId OperationId => _inner.OperationId;

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_admissionGate.TryEnter(out var admission))
        {
            return Task.FromResult(IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.AgentUnavailable,
                    IpcErrorCategory.Availability,
                    _localizer.GetString(AgentLocalizationKeys.IpcAdmissionClosed),
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false)));
        }

        admission!.Dispose();
        return _inner.HandleAsync(context, cancellationToken);
    }
}
