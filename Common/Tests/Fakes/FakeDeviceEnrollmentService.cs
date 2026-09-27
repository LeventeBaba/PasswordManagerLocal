using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Responses;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeviceEnrollmentService : IDeviceEnrollmentService
{
    public int CancelEnrollmentCalls { get; private set; }

    public Task<DeviceEnrollmentCodeResponse> StartEnrollmentAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DeviceEnrollmentStatusResponse> GetEnrollmentStatusAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task CancelEnrollmentAsync(CancellationToken ct = default)
    {
        CancelEnrollmentCalls++;
        return Task.CompletedTask;
    }

    public Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DeviceEnrollmentInfoResponse> GetIncomingEnrollmentInfoAsync(
        string sessionId,
        byte[] codeProof,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string> RegisterIncomingEnrollmentValidationFailureAsync(
        DeviceEnrollmentErrorCode errorCode,
        string message,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> CompleteIncomingEnrollmentAsync(
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
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}
