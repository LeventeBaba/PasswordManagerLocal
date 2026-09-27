namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public enum IpcOperationId
{
    Ping = 1,
    GetAgentStatus = 10,
    GetBackendRuntimeStatus = 11,
    GetInteractiveSessionStatus = 12,
    GetSynchronizationStatus = 13,
    RegisterUiConnection = 20,
    UnregisterUiConnection = 21,
    RequestUiOpen = 22,
    RequestUiActivation = 23,
    RequestAgentExit = 24,
    ResetDatabase = 25,
    GetBackgroundSyncState = 30,
    SetBackgroundSyncEnabled = 31,
    ReloadApplicationPreferences = 32,
    EndpointRpcRequest = 40,
    EndpointSessionReady = 41
}
