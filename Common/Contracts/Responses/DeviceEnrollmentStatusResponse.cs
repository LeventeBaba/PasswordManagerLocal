using PasswordManagerLocal.Common.Contracts.Enrollment;

namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class DeviceEnrollmentStatusResponse
{
    public DeviceEnrollmentState State { get; set; }
    public DeviceEnrollmentErrorCode ErrorCode { get; set; } = DeviceEnrollmentErrorCode.Unknown;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsCompleted => State == DeviceEnrollmentState.Completed;
    public bool IsFinished => State is DeviceEnrollmentState.Completed or DeviceEnrollmentState.Failed or DeviceEnrollmentState.Expired;
}
