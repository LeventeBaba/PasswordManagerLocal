using PasswordManagerLocal.Windows.Agent.Localization;

namespace PasswordManagerLocal.Windows.Agent.Tray;

public sealed record WindowsAgentTrayText(
    string ToolTip,
    string OpenLabel,
    string ExitLabel,
    string ErrorTitle,
    string StartupFailureTitle,
    string StartupFailureMessage,
    string ShutdownFailureTitle,
    string ShutdownFailureMessage)
{
    public static WindowsAgentTrayText Create(IAgentLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return new WindowsAgentTrayText(
            localizer.GetString(AgentLocalizationKeys.TrayTooltip),
            localizer.GetString(AgentLocalizationKeys.TrayOpen),
            localizer.GetString(AgentLocalizationKeys.TrayExit),
            localizer.GetString(AgentLocalizationKeys.TrayErrorTitle),
            localizer.GetString(AgentLocalizationKeys.StartupFailureTitle),
            localizer.GetString(AgentLocalizationKeys.StartupFailureMessage),
            localizer.GetString(AgentLocalizationKeys.ShutdownFailureTitle),
            localizer.GetString(AgentLocalizationKeys.ShutdownFailureMessage));
    }
}
