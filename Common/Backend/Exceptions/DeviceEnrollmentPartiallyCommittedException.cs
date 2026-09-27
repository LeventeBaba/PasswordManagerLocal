using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Exceptions;

public sealed class DeviceEnrollmentPartiallyCommittedException : MutationPartiallyCommittedException
{
    public DeviceEnrollmentPartiallyCommittedException(
        DeviceEnrollmentErrorCode errorCode,
        string message,
        Guid enrollmentCommitId,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        bool recoveryAvailable,
        bool transferPending,
        bool requiresSignedRemovalToUndo,
        bool requiresProcessRestart,
        Exception? innerException = null)
        : base(message, requiresProcessRestart, innerException)
    {
        ErrorCode = errorCode;
        EnrollmentCommitId = enrollmentCommitId;
        TargetDeviceId = targetDeviceId;
        TargetOriginInstanceId = targetOriginInstanceId;
        RecoveryAvailable = recoveryAvailable;
        TransferPending = transferPending;
        RequiresSignedRemovalToUndo = requiresSignedRemovalToUndo;
    }

    public DeviceEnrollmentErrorCode ErrorCode { get; }
    public Guid EnrollmentCommitId { get; }
    public Guid TargetDeviceId { get; }
    public Guid TargetOriginInstanceId { get; }
    public bool RecoveryAvailable { get; }
    public bool TransferPending { get; }
    public bool RequiresSignedRemovalToUndo { get; }
}
