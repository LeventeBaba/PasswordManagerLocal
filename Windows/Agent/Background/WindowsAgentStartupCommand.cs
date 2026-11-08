namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class WindowsAgentStartupCommand
{
    public WindowsAgentStartupCommand(string agentExecutablePath, bool unifiedExecutable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        var fullPath = Path.GetFullPath(agentExecutablePath);
        var expectedName = unifiedExecutable
            ? WindowsExecutableNames.UiExecutableFileName
            : WindowsStartupRegistrationConstants.AgentExecutableFileName;
        if (!Path.IsPathFullyQualified(fullPath) ||
            fullPath.Contains('"') ||
            !string.Equals(
                Path.GetFileName(fullPath),
                expectedName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Windows agent executable path is invalid.",
                nameof(agentExecutablePath));
        }

        AgentExecutablePath = fullPath;
        var roleArgument = unifiedExecutable ? WindowsAgentLaunchArguments.AgentRole + " " : string.Empty;
        Command = $"\"{AgentExecutablePath}\" {roleArgument}{WindowsStartupRegistrationConstants.BackgroundArgument}";
    }

    public string AgentExecutablePath { get; }
    public string Command { get; }
}
