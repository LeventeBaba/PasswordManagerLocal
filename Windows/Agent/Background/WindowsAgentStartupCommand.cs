namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class WindowsAgentStartupCommand
{
    public WindowsAgentStartupCommand(string agentExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        var fullPath = Path.GetFullPath(agentExecutablePath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            fullPath.Contains('"') ||
            !string.Equals(
                Path.GetFileName(fullPath),
                WindowsStartupRegistrationConstants.AgentExecutableFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Windows agent executable path is invalid.",
                nameof(agentExecutablePath));
        }

        AgentExecutablePath = fullPath;
        Command = $"\"{AgentExecutablePath}\" {WindowsStartupRegistrationConstants.BackgroundArgument}";
    }

    public string AgentExecutablePath { get; }
    public string Command { get; }
}
