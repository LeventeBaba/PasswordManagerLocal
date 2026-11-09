namespace PasswordManagerLocal.Windows.Ipc.Coordination.InstanceNaming;

public sealed record WindowsInstanceNames(
    string AgentLockFilePath,
    string UiLockFilePath,
    string ControlPipeName,
    string UiActivationPipeName,
    string EndpointPipeName);
