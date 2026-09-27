using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentCommandLineParser
{
    public WindowsAgentCommandLineOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            return new WindowsAgentCommandLineOptions(WindowsAgentLaunchMode.Manual);
        if (arguments.Count == 1 && string.Equals(
            arguments[0],
            WindowsAgentLaunchArguments.Background,
            StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsAgentCommandLineOptions(WindowsAgentLaunchMode.Background);
        }
        if (arguments.Count == 1 && string.Equals(
            arguments[0],
            WindowsAgentLaunchArguments.UiRequested,
            StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsAgentCommandLineOptions(WindowsAgentLaunchMode.UiRequested);
        }

        throw new ArgumentException("The Windows agent command line is invalid.", nameof(arguments));
    }
}
