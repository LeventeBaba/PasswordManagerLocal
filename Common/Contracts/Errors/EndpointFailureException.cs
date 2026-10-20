using PasswordManagerLocal.Common.Contracts.Enrollment;

namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class EndpointFailureException : Exception
{
    public EndpointFailureException(
        EndpointFailureKind failureKind,
        DeviceEnrollmentErrorCode? enrollmentErrorCode,
        Exception innerException)
        : base("The endpoint operation failed.", innerException)
    {
        FailureKind = failureKind;
        EnrollmentErrorCode = enrollmentErrorCode;
    }

    public EndpointFailureKind FailureKind { get; }
    public DeviceEnrollmentErrorCode? EnrollmentErrorCode { get; }
}
