using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public interface IWindowsAgentAdmissionGate
{
    AgentAdmissionState State { get; }
    bool IsOpen { get; }

    void Open();
    void ClosePermanently();
    bool TryEnter(out IDisposable? lease);
    Task WaitForDrainAsync(CancellationToken cancellationToken = default);
}