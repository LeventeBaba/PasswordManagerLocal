using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed class WindowsUiOpenService : IWindowsUiOpenService
{
    private readonly IWindowsUiActivationClient _activationClient;
    private readonly IWindowsUiLauncher _launcher;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly TimeSpan _launchCoalescingWindow;
    private readonly IAgentLocalizer _localizer;
    private DateTimeOffset? _lastLaunchRequestUtc;

    public WindowsUiOpenService(
        IWindowsUiActivationClient activationClient,
        IWindowsUiLauncher launcher,
        IAgentLocalizer localizer,
        TimeSpan? launchCoalescingWindow = null)
    {
        _activationClient = activationClient ?? throw new ArgumentNullException(nameof(activationClient));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _launchCoalescingWindow = launchCoalescingWindow ?? TimeSpan.FromSeconds(5);
        if (_launchCoalescingWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(launchCoalescingWindow));
    }

    public async Task<UiOpenResult> OpenAsync(
        UiActivationReason reason,
        CancellationToken cancellationToken = default)
    {
        await _openGate.WaitAsync(cancellationToken);
        try
        {
            var activation = await _activationClient.TryActivateAsync(
                new UiActivationRequestDto(reason, BringToForeground: true),
                cancellationToken);
            if (activation.Kind == UiActivationResultKind.Activated)
                return new UiOpenResult(UiOpenResultKind.Activated, _localizer.GetString(AgentLocalizationKeys.UiActivated));
            if (activation.Kind == UiActivationResultKind.Rejected)
                return new UiOpenResult(UiOpenResultKind.ActivationRejected, _localizer.GetString(AgentLocalizationKeys.UiActivationRejected));
            if (activation.Kind == UiActivationResultKind.Failed)
                return new UiOpenResult(UiOpenResultKind.ActivationFailed, _localizer.GetString(AgentLocalizationKeys.UiActivationFailed));

            var now = DateTimeOffset.UtcNow;
            if (_lastLaunchRequestUtc is { } last && now - last < _launchCoalescingWindow)
            {
                return new UiOpenResult(
                    UiOpenResultKind.LaunchRequested,
                    _localizer.GetString(AgentLocalizationKeys.UiLaunchAlreadyInProgress));
            }

            var launch = await _launcher.LaunchAsync(cancellationToken);
            if (launch.Kind == UiLaunchResultKind.LaunchRequested)
            {
                _lastLaunchRequestUtc = now;
                return new UiOpenResult(UiOpenResultKind.LaunchRequested, launch.SafeMessage);
            }

            return launch.Kind == UiLaunchResultKind.ExecutableNotFound
                ? new UiOpenResult(UiOpenResultKind.ExecutableNotFound, launch.SafeMessage)
                : new UiOpenResult(UiOpenResultKind.LaunchFailed, launch.SafeMessage);
        }
        finally
        {
            _openGate.Release();
        }
    }
}
