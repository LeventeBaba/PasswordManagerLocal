namespace PasswordManagerLocal.Android.Runtime;

internal enum AndroidInteractiveAttachmentState
{
    Initializing = 0,
    Active = 1,
    DatabaseRecovery = 2,
    Resetting = 3,
    Closing = 4,
    Disposed = 5
}
