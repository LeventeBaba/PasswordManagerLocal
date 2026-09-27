namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

internal enum EndpointInvocationStage
{
    BeforeInvocation = 1,
    Invoking = 2,
    InvocationCompleted = 3,
    ResponseValidated = 4,
    ResponseSerialized = 5
}
