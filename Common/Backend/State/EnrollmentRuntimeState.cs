
using PasswordManagerLocal.Common.Backend.Abstractions.State;

namespace PasswordManagerLocal.Common.Backend.State;

public sealed class EnrollmentRuntimeState : IEnrollmentRuntimeState
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void Activate() => Interlocked.Exchange(ref _active, 1);

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);
}
