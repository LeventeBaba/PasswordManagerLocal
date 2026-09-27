namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed record EndpointRecoveryMetadata(
    EndpointRecoveryKind RecoveryKind,
    Guid TargetDeviceId,
    Guid TargetOriginInstanceId,
    Guid EnrollmentCommitId,
    bool RecoveryAvailable,
    bool TransferPending,
    bool RequiresSignedRemovalToUndo);
