using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Responses;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentService
{
    Task<DeviceEnrollmentCodeResponse> StartEnrollmentAsync(CancellationToken ct = default);
    Task<DeviceEnrollmentStatusResponse> GetEnrollmentStatusAsync(CancellationToken ct = default);
    Task CancelEnrollmentAsync(CancellationToken ct = default);
    Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default);
    Task<DeviceEnrollmentInfoResponse> GetIncomingEnrollmentInfoAsync(string sessionId, byte[] codeProof, CancellationToken ct = default);
    Task<string> RegisterIncomingEnrollmentValidationFailureAsync(DeviceEnrollmentErrorCode errorCode, string message, CancellationToken ct = default);
    Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> CompleteIncomingEnrollmentAsync(
        string sessionId,
        byte[] codeProof,
        byte[] snapshot,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsCertFingerprint,
        string actualClientTlsCertFingerprint,
        string? sourceHost,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        int snapshotEncryptionVersion,
        byte[] snapshotEncryptionNonce,
        byte[] snapshotEncryptionTag,
        CancellationToken ct = default);
}
