namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>UI-session-owned adapters. No process-static Activity, view or delegate.</summary>
public sealed class FrontendPlatformServices : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _deactivated;
    private int _disposed;
    private Func<bool>? _animationsEnabledProvider;

    public FrontendPlatformServices()
    {
        LifetimeToken = _lifetime.Token;
        Clipboard = new ClipboardService(LifetimeToken);
        ImagePicker = new QrImagePickerService(LifetimeToken);
    }

    public CancellationToken LifetimeToken { get; }
    public ClipboardService Clipboard { get; }
    public QrImagePickerService ImagePicker { get; }
    public SoftwareKeyboardService Keyboard { get; } = new();
    public EnrollmentQrCodeCameraScannerService CameraScanner { get; } = new();
    public SensitiveDataVisibilityService SensitiveData { get; } = new();

    public bool AreAnimationsEnabled
    {
        get
        {
            try
            {
                return _animationsEnabledProvider?.Invoke() ?? true;
            }
            catch
            {
                return true;
            }
        }
    }

    public void SetPlatformAnimationsEnabledProvider(Func<bool>? provider) =>
        _animationsEnabledProvider = provider;

    public void Deactivate()
    {
        if (Interlocked.Exchange(ref _deactivated, 1) != 0)
            return;
        _lifetime.Cancel();
        Clipboard.SetPlatformClipboardWriter(null);
        Clipboard.SetActiveTopLevel(null);
        ImagePicker.SetActiveTopLevel(null);
        Keyboard.SetPlatformHideAction(null);
        CameraScanner.SetPlatformScanner(null);
        SensitiveData.ClearSubscribers();
        _animationsEnabledProvider = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Deactivate();
        _lifetime.Dispose();
    }
}
