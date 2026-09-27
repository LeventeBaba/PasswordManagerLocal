using Android.Content;
using Android.OS;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidRuntimeServiceConnection : Java.Lang.Object, IServiceConnection
{
    private readonly TaskCompletionSource<PasswordManagerBackgroundService> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnServiceConnected(ComponentName? name, IBinder? service)
    {
        if (service is PasswordManagerBackgroundServiceBinder binder)
        {
            _completion.TrySetResult(binder.Service);
            return;
        }

        _completion.TrySetException(
            new InvalidOperationException("The Android runtime service returned an unexpected binder."));
    }

    public void OnServiceDisconnected(ComponentName? name)
    {
        _completion.TrySetException(
            new InvalidOperationException("The Android runtime service disconnected before attachment completed."));
    }

    public Task<PasswordManagerBackgroundService> WaitForServiceAsync(
        CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);
}
