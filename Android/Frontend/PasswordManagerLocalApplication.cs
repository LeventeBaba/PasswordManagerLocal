using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;
using PasswordManagerLocal.Common.Frontend;
using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Android.Frontend;

[Application]
public sealed class PasswordManagerLocalApplication : AvaloniaAndroidApplication<App>
{
    public PasswordManagerLocalApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public AndroidRuntimeServiceConnector RuntimeServiceConnector { get; private set; } = null!;
    public string ApplicationDataDirectory { get; private set; } = string.Empty;

    public override void OnCreate()
    {
        var filesDirectory = FilesDir?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(filesDirectory))
            throw new InvalidOperationException("The Android application-data directory is unavailable.");

        ApplicationDataDirectory = Path.Combine(
            filesDirectory,
            ApplicationFileNames.AppFolderName);
        RuntimeServiceConnector = new AndroidRuntimeServiceConnector();
        base.OnCreate();
    }
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseHarfBuzz()
            .UseReactiveUI(_ => { });

}
