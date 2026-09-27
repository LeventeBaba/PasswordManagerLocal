using Android.Content;
using Android.OS;
using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidSecureStorageAvailability : IAndroidSecureStorageAvailability
{
    private readonly Context _applicationContext;

    public AndroidSecureStorageAvailability(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _applicationContext = context.ApplicationContext
            ?? throw new InvalidOperationException("The Android application context is unavailable.");
    }

    public bool IsAvailable
    {
        get
        {
            var userManager = _applicationContext.GetSystemService(Context.UserService) as UserManager;
            return userManager?.IsUserUnlocked == true;
        }
    }
}
