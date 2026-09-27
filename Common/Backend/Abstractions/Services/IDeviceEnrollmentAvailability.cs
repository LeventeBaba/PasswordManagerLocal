namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentAvailability
{
    bool IsEnrollmentAllowed { get; }
}
