namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed record EndpointRpcError(
    EndpointRpcErrorCode ErrorCode,
    EndpointRpcErrorCategory ErrorCategory,
    string SafeMessage,
    long CorrelationId,
    DateTimeOffset OccurredAtUtc,
    bool IsRetryable,
    bool RequiresProcessRestart,
    EndpointMutationOutcome MutationOutcome,
    EndpointRecoveryMetadata? Recovery,
    IReadOnlyList<string>? ValidationErrors = null);
