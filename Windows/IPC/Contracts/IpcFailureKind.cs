namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum IpcFailureKind
{
    None = 0,
    Runtime = 1,
    InteractiveCleanup = 2,
    Synchronization = 3,
    Storage = 4,
    PlatformKey = 5,
    Protocol = 6,
    AgentShell = 7,
    UiActivation = 8,
    UiLaunch = 9,
    BackgroundConfiguration = 10
}
