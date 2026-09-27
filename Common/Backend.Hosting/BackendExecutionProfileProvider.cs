using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal sealed class BackendExecutionProfileProvider :
    IBackendExecutionProfileProvider,
    IBackendExecutionProfileProviderLifecycle
{
    private static readonly BackendExecutionProfile InteractiveProfile = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(35));

    private static readonly BackendExecutionProfile BackgroundProfile = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(125));

    private readonly IBackendRuntimeLifetimeCoordinator _lifetimeCoordinator;
    private readonly object _stateLock = new();
    private BackendExecutionProfile? _current;
    private bool _isInteractive;
    private bool _enrollmentAdmissionOpen;
    private bool _profileChangePending;
    private bool _stopped;

    public BackendExecutionProfileProvider(
        IBackendRuntimeLifetimeCoordinator lifetimeCoordinator)
    {
        _lifetimeCoordinator = lifetimeCoordinator
            ?? throw new ArgumentNullException(nameof(lifetimeCoordinator));
        _lifetimeCoordinator.ActiveReasonsChanged += HandleActiveReasonsChanged;
    }

    public BackendExecutionProfile? Current
    {
        get
        {
            lock (_stateLock)
                return _current;
        }
    }

    public bool IsInteractive
    {
        get
        {
            lock (_stateLock)
                return _isInteractive;
        }
    }

    public bool IsEnrollmentAllowed
    {
        get
        {
            lock (_stateLock)
                return !_stopped && _isInteractive && _enrollmentAdmissionOpen;
        }
    }

    public event EventHandler? ProfileChanged;

    void IBackendExecutionProfileProviderLifecycle.InitializeCurrent() =>
        Refresh(publishChange: false);

    void IBackendExecutionProfileProviderLifecycle.OpenEnrollmentAdmission()
    {
        lock (_stateLock)
        {
            if (_stopped || !_isInteractive)
                throw new InvalidOperationException(
                    "Enrollment admission cannot open without an active interactive runtime reason.");

            _enrollmentAdmissionOpen = true;
        }
    }

    void IBackendExecutionProfileProviderLifecycle.CloseEnrollmentAdmission()
    {
        lock (_stateLock)
            _enrollmentAdmissionOpen = false;
    }

    void IBackendExecutionProfileProviderLifecycle.StopPublishing()
    {
        lock (_stateLock)
        {
            if (_stopped)
                return;

            _stopped = true;
            _current = null;
            _isInteractive = false;
            _enrollmentAdmissionOpen = false;
            _profileChangePending = false;
        }

        _lifetimeCoordinator.ActiveReasonsChanged -= HandleActiveReasonsChanged;
        ProfileChanged = null;
    }

    private void HandleActiveReasonsChanged(object? sender, EventArgs args) =>
        Refresh(publishChange: true);

    private void Refresh(bool publishChange)
    {
        var reasons = _lifetimeCoordinator.ActiveReasons;
        var isInteractive = reasons.HasFlag(BackendLifetimeReason.InteractiveUi);
        var next = isInteractive
            ? InteractiveProfile
            : reasons.HasFlag(BackendLifetimeReason.BackgroundSync)
                ? BackgroundProfile
                : null;

        var shouldPublish = false;
        lock (_stateLock)
        {
            if (_stopped)
                return;

            if (_current != next || _isInteractive != isInteractive)
            {
                _current = next;
                _isInteractive = isInteractive;
                if (!isInteractive)
                    _enrollmentAdmissionOpen = false;
                _profileChangePending = true;
            }

            if (publishChange && _profileChangePending)
            {
                _profileChangePending = false;
                shouldPublish = true;
            }
        }

        if (!shouldPublish)
            return;

        var handlers = ProfileChanged;
        if (handlers is null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }
}
