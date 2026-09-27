namespace PasswordManagerLocal.Android.Runtime;

public interface IAndroidForegroundServiceController
{
    void EnsureServiceStarted();
    AndroidForegroundEntryResult EnterForeground(AndroidForegroundNotificationState state);
    void ExitForeground();
    void RequestStop();
    void RequestProcessTermination();
}
