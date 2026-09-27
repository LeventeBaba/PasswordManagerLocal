using Android.OS;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class PasswordManagerBackgroundServiceBinder : Binder
{
    public PasswordManagerBackgroundServiceBinder(PasswordManagerBackgroundService service)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public PasswordManagerBackgroundService Service { get; }
}
