using PasswordManagerLocal.Windows.Frontend.Activation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsWindowActivationTarget : IWindowsWindowActivationTarget
{
    private readonly Func<bool> _isOnUiThread;

    public FakeWindowsWindowActivationTarget(Func<bool>? isOnUiThread = null)
    {
        _isOnUiThread = isOnUiThread ?? (() => true);
    }

    public bool IsMinimized { get; set; }
    public bool IsVisible { get; set; } = true;
    public int RestoreCount { get; private set; }
    public int ShowCount { get; private set; }
    public int ActivateCount { get; private set; }
    public int FocusCount { get; private set; }
    public bool AllCallsWereDispatched { get; private set; } = true;

    public void Restore()
    {
        RecordDispatch();
        RestoreCount++;
        IsMinimized = false;
    }

    public void Show()
    {
        RecordDispatch();
        ShowCount++;
        IsVisible = true;
    }

    public void Activate()
    {
        RecordDispatch();
        ActivateCount++;
    }

    public void Focus()
    {
        RecordDispatch();
        FocusCount++;
    }

    private void RecordDispatch() =>
        AllCallsWereDispatched &= _isOnUiThread();
}
