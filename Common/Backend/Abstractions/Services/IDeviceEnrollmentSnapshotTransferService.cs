using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentSnapshotTransferService
{
    Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> SendAsync(
        EnrollmentEndpoint endpoint,
        string sessionId,
        byte[] secret,
        byte[] proof,
        DeviceEnrollmentSnapshot snapshot,
        CancellationToken ct = default);
}
