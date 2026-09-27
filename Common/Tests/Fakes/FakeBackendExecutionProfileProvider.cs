using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeBackendExecutionProfileProvider : IBackendExecutionProfileProvider
{
    public BackendExecutionProfile? Current { get; private set; }
    public bool IsInteractive { get; private set; }
    public bool IsEnrollmentAllowed => IsInteractive;

    public event EventHandler? ProfileChanged;

    public void SetProfile(BackendExecutionProfile? profile, bool isInteractive)
    {
        if (Current == profile && IsInteractive == isInteractive)
            return;

        Current = profile;
        IsInteractive = isInteractive;
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}
