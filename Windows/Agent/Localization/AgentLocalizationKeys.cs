namespace PasswordManagerLocal.Windows.Agent.Localization;

public static class AgentLocalizationKeys
{
    public const string TrayTooltip = "Agent.Tray.Tooltip";
    public const string TrayOpen = "Agent.Tray.Open";
    public const string TrayExit = "Agent.Tray.Exit";
    public const string TrayErrorTitle = "Agent.Tray.ErrorTitle";
    public const string StartupFailureTitle = "Agent.StartupFailure.Title";
    public const string StartupFailureMessage = "Agent.StartupFailure.Message";
    public const string ShutdownFailureTitle = "Agent.ShutdownFailure.Title";
    public const string ShutdownFailureMessage = "Agent.ShutdownFailure.Message";
    public const string RuntimeShellStartFailed = "Agent.Runtime.ShellStartFailed";
    public const string RuntimeControlListenerFailed = "Agent.Runtime.ControlListenerFailed";
    public const string RuntimeEndpointListenerFailed = "Agent.Runtime.EndpointListenerFailed";
    public const string RuntimeBackendFailed = "Agent.Runtime.BackendFailed";
    public const string RuntimeBackendRestartRequired = "Agent.Runtime.BackendRestartRequired";
    public const string RuntimeBackendRecreationUnsafe = "Agent.Runtime.BackendRecreationUnsafe";
    public const string RuntimeInteractiveCleanupFailed = "Agent.Runtime.InteractiveCleanupFailed";
    public const string RuntimeBackgroundSyncDegraded = "Agent.Runtime.BackgroundSyncDegraded";
    public const string RuntimePlatformKeyUnavailable = "Agent.Runtime.PlatformKeyUnavailable";
    public const string RuntimeStorageUnavailable = "Agent.Runtime.StorageUnavailable";
    public const string RuntimeDatabaseVersionUnsupported = "Agent.Runtime.DatabaseVersionUnsupported";
    public const string RuntimeShutdownShellCleanupFailed = "Agent.Runtime.ShutdownShellCleanupFailed";
    public const string RuntimeShutdownResourcesFailed = "Agent.Runtime.ShutdownResourcesFailed";
    public const string BackgroundReadFailed = "Agent.Background.ReadFailed";
    public const string BackgroundDisabledStartupRestoreFailed = "Agent.Background.DisabledStartupRestoreFailed";
    public const string BackgroundDisabledLeaseReleaseFailed = "Agent.Background.DisabledLeaseReleaseFailed";
    public const string BackgroundStartupVerifyFailed = "Agent.Background.StartupVerifyFailed";
    public const string BackgroundStartupRestoreFailed = "Agent.Background.StartupRestoreFailed";
    public const string BackgroundRuntimeStartFailed = "Agent.Background.RuntimeStartFailed";
    public const string BackgroundResetRestoreFailed = "Agent.Background.ResetRestoreFailed";
    public const string BackgroundStartupCommandMismatch = "Agent.Background.StartupCommandMismatch";
    public const string BackgroundNotOperational = "Agent.Background.NotOperational";
    public const string BackgroundEnableRolledBack = "Agent.Background.EnableRolledBack";
    public const string BackgroundEnableRollbackFailed = "Agent.Background.EnableRollbackFailed";
    public const string BackgroundDisableNotReached = "Agent.Background.DisableNotReached";
    public const string BackgroundDisableRolledBack = "Agent.Background.DisableRolledBack";
    public const string BackgroundDisableRollbackFailed = "Agent.Background.DisableRollbackFailed";
    public const string BackgroundSettingUnavailable = "Agent.Background.SettingUnavailable";
    public const string BackgroundStartupUnavailable = "Agent.Background.StartupUnavailable";
    public const string BackgroundStartupStateMismatch = "Agent.Background.StartupStateMismatch";
    public const string BackgroundSettingLeaseMismatch = "Agent.Background.SettingLeaseMismatch";
    public const string BackgroundEnabledNotOperational = "Agent.Background.EnabledNotOperational";
    public const string BackgroundAgentUnavailable = "Agent.Background.AgentUnavailable";
    public const string BackgroundStartupAccessDenied = "Agent.Background.StartupAccessDenied";
    public const string BackgroundSettingInvalid = "Agent.Background.SettingInvalid";
    public const string DatabaseResetInProgress = "Agent.Database.ResetInProgress";
    public const string DatabaseRestartRequired = "Agent.Database.RestartRequired";
    public const string DatabaseCompatibilityRequired = "Agent.Database.CompatibilityRequired";
    public const string DatabaseResetFailed = "Agent.Database.ResetFailed";
    public const string UiActivated = "Agent.Ui.Activated";
    public const string UiActivationRejected = "Agent.Ui.ActivationRejected";
    public const string UiActivationFailed = "Agent.Ui.ActivationFailed";
    public const string UiExecutableNotFound = "Agent.Ui.ExecutableNotFound";
    public const string UiLaunchRequested = "Agent.Ui.LaunchRequested";
    public const string UiLaunchFailed = "Agent.Ui.LaunchFailed";
    public const string UiLaunchAlreadyInProgress = "Agent.Ui.LaunchAlreadyInProgress";
    public const string UiCloseAcknowledged = "Agent.Ui.CloseAcknowledged";
    public const string UiCloseRejected = "Agent.Ui.CloseRejected";
    public const string UiActivationUnavailable = "Agent.Ui.ActivationUnavailable";
    public const string UiCloseNotAcknowledged = "Agent.Ui.CloseNotAcknowledged";
    public const string UiCloseTimedOut = "Agent.Ui.CloseTimedOut";
    public const string ExitNotAvailable = "Agent.Exit.NotAvailable";
    public const string ExitNoLongerAvailable = "Agent.Exit.NoLongerAvailable";
    public const string ExitAlreadyInProgress = "Agent.Exit.AlreadyInProgress";
    public const string ExitUiReconnecting = "Agent.Exit.UiReconnecting";
    public const string ExitUiPresenceUnknown = "Agent.Exit.UiPresenceUnknown";
    public const string ExitUiAcknowledgementFailed = "Agent.Exit.UiAcknowledgementFailed";
    public const string IpcOperationUnavailable = "Agent.Ipc.OperationUnavailable";
    public const string IpcAdmissionClosed = "Agent.Ipc.AdmissionClosed";
    public const string IpcUnauthorizedOperation = "Agent.Ipc.UnauthorizedOperation";
    public const string IpcUiRegistrationRequired = "Agent.Ipc.UiRegistrationRequired";
    public const string IpcRegisteredUiRequired = "Agent.Ipc.RegisteredUiRequired";

    public static IReadOnlySet<string> All { get; } = typeof(AgentLocalizationKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}
