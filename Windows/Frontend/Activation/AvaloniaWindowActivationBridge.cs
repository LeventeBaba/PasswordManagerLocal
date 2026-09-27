namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class AvaloniaWindowActivationBridge : IWindowsWindowActivationBridge
{
    private readonly IWindowsUiDispatcher _dispatcher;
    private readonly IWindowsWindowActivationTargetProvider _targetProvider;

    public AvaloniaWindowActivationBridge()
        : this(new AvaloniaUiDispatcher(), new AvaloniaWindowActivationTargetProvider())
    {
    }

    public AvaloniaWindowActivationBridge(
        IWindowsUiDispatcher dispatcher,
        IWindowsWindowActivationTargetProvider targetProvider)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
    }

    public Task<bool> ActivateAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            var target = _targetProvider.GetTarget();
            if (target is null)
                return false;

            if (target.IsMinimized)
                target.Restore();
            if (!target.IsVisible)
                target.Show();

            target.Activate();
            target.Focus();
            return true;
        }, cancellationToken);
}
