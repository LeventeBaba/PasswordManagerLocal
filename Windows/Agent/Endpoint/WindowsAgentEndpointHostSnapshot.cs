namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public sealed record WindowsAgentEndpointHostSnapshot(
    WindowsAgentEndpointHostState State,
    Exception? Failure,
    DateTimeOffset ChangedAtUtc);
