using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Responses;
using System.Security.Cryptography;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.State;

internal sealed class EnrollmentSession
{
    public string SessionId { get; set; } = string.Empty;
    public byte[] Secret { get; set; } = [];
    public string Code { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DeviceEnrollmentState State { get; set; } = DeviceEnrollmentState.Waiting;
    public string? ErrorMessage { get; set; }
    public DeviceEnrollmentErrorCode ErrorCode { get; set; } = DeviceEnrollmentErrorCode.Unknown;
    public int FailedValidationAttempts { get; set; }

    public void ClearSensitiveData()
    {
        if (Secret.Length > 0)
            CryptographicOperations.ZeroMemory(Secret);

        Secret = [];
        Code = string.Empty;
    }
}
