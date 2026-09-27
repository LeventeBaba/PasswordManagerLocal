using Android.App;
using Android.Content;
using PasswordManagerLocal.Common.Frontend.Services;
using AndroidClipboardManager = Android.Content.ClipboardManager;
using System;
using System.Threading.Tasks;

namespace PasswordManagerLocal.Android.Frontend;

internal sealed class AndroidClipboardWriter : IClipboardWriter
{
    private readonly Activity _activity;

    public AndroidClipboardWriter(Activity activity)
    {
        _activity = activity;
    }



    public Task<bool> TrySetTextAsync(string text)
    {
        var completionSource = new TaskCompletionSource<bool>();

        _activity.RunOnUiThread(() =>
        {
            try
            {
                var clipboardManager = _activity.GetSystemService(Context.ClipboardService) as AndroidClipboardManager;

                if (clipboardManager is null)
                {
                    completionSource.TrySetResult(false);
                    return;
                }

                var clipData = ClipData.NewPlainText("PasswordManagerLocal", text);
                clipboardManager.PrimaryClip = clipData;
                completionSource.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });

        return completionSource.Task;
    }
}
