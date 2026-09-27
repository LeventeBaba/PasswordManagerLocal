namespace PasswordManagerLocal.Common.Backend.Services;

internal sealed class DeviceEnrollmentOperationLease : IDisposable
{
    private DeviceEnrollmentService? _owner;

    public DeviceEnrollmentOperationLease(
        DeviceEnrollmentService owner,
        CancellationToken admissionCancellationToken)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        AdmissionCancellationToken = admissionCancellationToken;
    }

    public CancellationToken AdmissionCancellationToken { get; }

    public void EnterCriticalSection()
    {
        var owner = Volatile.Read(ref _owner)
            ?? throw new ObjectDisposedException(nameof(DeviceEnrollmentOperationLease));
        owner.EnterCriticalEnrollmentSection();
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.CompleteEnrollmentOperation();
    }
}
