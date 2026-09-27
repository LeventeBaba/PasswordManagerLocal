using PasswordManagerLocal.Windows.Agent.Ui;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DelegateProcessLauncher : IProcessLauncher
{
    private readonly Func<ProcessStartInfo, bool> _start;

    public DelegateProcessLauncher(Func<ProcessStartInfo, bool> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public int StartCount { get; private set; }

    public bool TryStart(ProcessStartInfo startInfo)
    {
        StartCount++;
        return _start(startInfo);
    }
}
