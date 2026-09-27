namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public enum WindowsAgentEndpointHostState
{
    Stopped = 0,
    Starting = 1,
    Ready = 2,
    Stopping = 3,
    Failed = 4
}
