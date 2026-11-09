using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed record EndpointRpcError(
    EndpointRpcErrorCode ErrorCode,
    EndpointFailureKind FailureKind,
    string SafeMessage,
    bool RequiresProcessRestart,
    EndpointMutationOutcome MutationOutcome,
    DeviceEnrollmentErrorCode? EnrollmentErrorCode = null);
