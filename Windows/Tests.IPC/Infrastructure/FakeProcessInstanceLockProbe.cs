using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeProcessInstanceLockProbe : IProcessInstanceLockProbe
{
    private readonly Queue<ProcessInstanceLockProbeResult> _results = new();

    public ProcessInstanceLockProbeResult Result { get; set; } = ProcessInstanceLockProbeResult.Free;
    public int ProbeCount { get; private set; }

    public void Enqueue(ProcessInstanceLockProbeResult result) => _results.Enqueue(result);

    public ProcessInstanceLockProbeResult Probe()
    {
        ProbeCount++;
        return _results.Count == 0 ? Result : _results.Dequeue();
    }
}
