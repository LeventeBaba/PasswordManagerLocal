using Android.App;
using Android.Content;
using Android.OS;
using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Android.Frontend;

[BroadcastReceiver(
    Name = "com.levibaba.passwordmanagerlocal.AndroidBackgroundRestorationReceiver",
    Enabled = true,
    Exported = false)]
[IntentFilter(new[]
{
    Intent.ActionBootCompleted,
    Intent.ActionMyPackageReplaced
})]
public sealed class AndroidBackgroundRestorationReceiver : BroadcastReceiver
{
    private readonly AndroidBackgroundRestorationPolicy _policy = new();

    public override void OnReceive(Context? context, Intent? intent)
    {
        var trigger = GetTrigger(intent?.Action);
        if (context is null || trigger == AndroidBackgroundRestorationTrigger.Unsupported)
            return;

        var pendingResult = GoAsync();
        _ = RestoreAsync(context.ApplicationContext ?? context, trigger, pendingResult);
    }

    private async Task RestoreAsync(
        Context context,
        AndroidBackgroundRestorationTrigger trigger,
        BroadcastReceiver.PendingResult pendingResult)
    {
        try
        {
            var userManager = context.GetSystemService(Context.UserService) as UserManager;
            var initialDecision = _policy.Decide(
                trigger,
                userManager?.IsUserUnlocked == true,
                isBackgroundEnabled: null);
            if (!initialDecision.ShouldReadPersistedSetting)
                return;

            var filesDirectory = context.FilesDir?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(filesDirectory))
                return;

            var store = new FileBackgroundSyncSettingsStore(Path.Combine(
                filesDirectory,
                ApplicationFileNames.AppFolderName));
            using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var settings = await store.ReadAsync(timeoutSource.Token);
            var finalDecision = _policy.Decide(
                trigger,
                isUserUnlocked: true,
                settings.IsEnabled);
            if (!finalDecision.ShouldRequestServiceStart)
                return;

            var serviceIntent = new Intent(context, typeof(PasswordManagerBackgroundService));
            context.StartForegroundService(serviceIntent);
        }
        catch
        {
        }
        finally
        {
            pendingResult.Finish();
        }
    }

    private AndroidBackgroundRestorationTrigger GetTrigger(string? action) => action switch
    {
        Intent.ActionBootCompleted => AndroidBackgroundRestorationTrigger.BootCompleted,
        Intent.ActionMyPackageReplaced => AndroidBackgroundRestorationTrigger.PackageReplaced,
        _ => AndroidBackgroundRestorationTrigger.Unsupported
    };
}
