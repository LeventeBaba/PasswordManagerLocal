namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed record WindowsInstanceNames(
    string AgentLockFilePath,
    string UiLockFilePath,
    string ControlPipeName,
    string UiActivationPipeName,
    string EndpointPipeName);
