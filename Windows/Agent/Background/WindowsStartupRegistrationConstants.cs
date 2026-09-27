using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Agent.Background;

public static class WindowsStartupRegistrationConstants
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PasswordManagerLocal.Agent";
    public const string AgentExecutableFileName = "PasswordManagerLocal.Windows.Agent.exe";
    public const string BackgroundArgument = WindowsAgentLaunchArguments.Background;
}
