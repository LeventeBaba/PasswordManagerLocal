using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Frontend.SingleInstance;

public sealed class WindowsUiSingleInstanceController
{
    private readonly IProcessInstanceLock _processLock;
    private readonly IWindowsUiActivationClient _activationClient;
    private readonly int _maximumActivationAttempts;
    private readonly TimeSpan _retryDelay;

    public WindowsUiSingleInstanceController(
        IProcessInstanceLock processLock,
        IWindowsUiActivationClient activationClient,
        int maximumActivationAttempts = 10,
        TimeSpan? retryDelay = null)
    {
        _processLock = processLock ?? throw new ArgumentNullException(nameof(processLock));
        _activationClient = activationClient ?? throw new ArgumentNullException(nameof(activationClient));
        if (maximumActivationAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumActivationAttempts));
        _maximumActivationAttempts = maximumActivationAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        if (_retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
    }

    public async Task<WindowsUiInstanceRole> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        if (_processLock.IsOwner)
            return ConfirmPrimaryOwnership();

        for (var attempt = 0; attempt < _maximumActivationAttempts; attempt++)
        {
            if (_processLock.TryAcquire())
                return ConfirmPrimaryOwnership();

            var result = await _activationClient.TryActivateAsync(
                new UiActivationRequestDto(
                    UiActivationReason.UserLaunch,
                    BringToForeground: true),
                cancellationToken);
            if (result.Kind is UiActivationResultKind.Activated or
                UiActivationResultKind.Rejected)
            {
                return WindowsUiInstanceRole.SecondaryActivationRequested;
            }

            if (_processLock.TryAcquire())
                return ConfirmPrimaryOwnership();

            if (attempt + 1 < _maximumActivationAttempts && _retryDelay > TimeSpan.Zero)
                await Task.Delay(_retryDelay, cancellationToken);
        }

        return WindowsUiInstanceRole.SecondaryActivationUnavailable;
    }

    private WindowsUiInstanceRole ConfirmPrimaryOwnership()
    {
        _processLock.EnsureOwnership();
        return WindowsUiInstanceRole.Primary;
    }
}
