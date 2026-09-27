using PasswordManagerLocal.Windows.Agent.Tray;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class SetBackgroundSyncEnabledWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsBackgroundSyncCoordinator _coordinator;
    private readonly WindowsIpcContractValidator _validator;
    private readonly ITrayIconController? _trayIcon;

    public SetBackgroundSyncEnabledWindowsIpcRequestHandler(
        IWindowsBackgroundSyncCoordinator coordinator,
        WindowsIpcContractValidator? validator = null,
        ITrayIconController? trayIcon = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _validator = validator ?? new WindowsIpcContractValidator();
        _trayIcon = trayIcon;
    }

    public IpcOperationId OperationId => IpcOperationId.SetBackgroundSyncEnabled;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var request = context.GetRequiredPayload(
            WindowsIpcJsonContext.Default.SetBackgroundSyncEnabledRequestDto);
        var state = await _coordinator.SetEnabledAsync(request.IsEnabled, cancellationToken);
        if (_trayIcon is not null)
            await _trayIcon.SetVisibleAsync(state.IsEnabled, CancellationToken.None);
        _validator.Validate(state);
        return context.Success(state, WindowsIpcJsonContext.Default.WindowsBackgroundSyncStateDto);
    }
}
