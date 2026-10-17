namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Logical frontend sections whose cached projections can be reconciled independently.</summary>
public enum FrontendRefreshPage
{
    Passwords,
    UserProfile,
    Devices,
    Enrollment,
    AuthenticationSessions,
    LocalDevice,
    BackgroundSync
}
