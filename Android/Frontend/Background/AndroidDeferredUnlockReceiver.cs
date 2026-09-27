using Android.Content;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidDeferredUnlockReceiver : BroadcastReceiver
{
    private readonly Action _queueRestoration;

    public AndroidDeferredUnlockReceiver(Action queueRestoration)
    {
        _queueRestoration = queueRestoration ?? throw new ArgumentNullException(nameof(queueRestoration));
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionUserUnlocked)
            _queueRestoration();
    }
}
