namespace PasswordManagerLocal.Common.Contracts.Notifications;

[Flags]
public enum FrontendChangeScope
{
    None = 0,
    AuthenticationSessions = 1 << 0,
    UserProfile = 1 << 1,
    Passwords = 1 << 2,
    Devices = 1 << 3,
    DevicePresence = 1 << 4,
    Enrollment = 1 << 5,
    LocalDevice = 1 << 6,
    BackgroundSync = 1 << 7,
    AllAuthenticatedData = UserProfile | Passwords | Devices,
    Everything = AuthenticationSessions | UserProfile | Passwords | Devices | DevicePresence | Enrollment | LocalDevice | BackgroundSync
}
