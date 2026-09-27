namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointRpcErrorCode
{
    AuthenticationFailed = 1,
    AuthorizationFailed = 2,
    ValidationFailed = 3,
    NotFound = 4,
    Conflict = 5,
    OperationRejected = 6,
    InteractiveSessionUnavailable = 7,
    RuntimeUnavailable = 8,
    OperationCancelled = 9,
    OperationOutcomeUnknown = 10,
    RequestPayloadTooLarge = 11,
    ResponsePayloadTooLarge = 12,
    BackendFailure = 13,
    UnknownOperation = 14,
    Disconnected = 15,
    EndpointCorrelationMismatch = 16,
    OperationPartiallyCommitted = 17
}
