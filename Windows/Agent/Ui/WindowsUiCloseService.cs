using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed class WindowsUiCloseService : IWindowsUiCloseService
{
    private readonly IWindowsUiActivationClient _activationClient;
    private readonly TimeSpan _acknowledgementTimeout;
    private readonly IAgentLocalizer _localizer;

    public WindowsUiCloseService(
        IWindowsUiActivationClient activationClient,
        IAgentLocalizer localizer,
        TimeSpan? acknowledgementTimeout = null)
    {
        _activationClient = activationClient ?? throw new ArgumentNullException(nameof(activationClient));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _acknowledgementTimeout = acknowledgementTimeout ?? TimeSpan.FromSeconds(3);
        if (_acknowledgementTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(acknowledgementTimeout));
    }

    public async Task<WindowsUiCloseResult> RequestIntentionalShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(_acknowledgementTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            var result = await _activationClient.TryActivateAsync(
                new UiActivationRequestDto(
                    UiActivationReason.AgentRequest,
                    BringToForeground: false,
                    UiActivationCommand.IntentionalAgentShutdown),
                linkedSource.Token);
            return result.Kind switch
            {
                UiActivationResultKind.Activated => new WindowsUiCloseResult(
                    WindowsUiCloseResultKind.Acknowledged,
                    _localizer.GetString(AgentLocalizationKeys.UiCloseAcknowledged)),
                UiActivationResultKind.Rejected => new WindowsUiCloseResult(
                    WindowsUiCloseResultKind.Rejected,
                    _localizer.GetString(AgentLocalizationKeys.UiCloseRejected)),
                UiActivationResultKind.Unavailable => new WindowsUiCloseResult(
                    WindowsUiCloseResultKind.Unavailable,
                    _localizer.GetString(AgentLocalizationKeys.UiActivationUnavailable)),
                _ => new WindowsUiCloseResult(
                    WindowsUiCloseResultKind.Failed,
                    _localizer.GetString(AgentLocalizationKeys.UiCloseNotAcknowledged))
            };
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return new WindowsUiCloseResult(
                WindowsUiCloseResultKind.Failed,
                _localizer.GetString(AgentLocalizationKeys.UiCloseTimedOut));
        }
    }
}
