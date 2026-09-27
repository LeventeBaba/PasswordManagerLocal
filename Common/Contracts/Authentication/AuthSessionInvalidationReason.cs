namespace PasswordManagerLocal.Common.Contracts.Authentication;

public enum AuthSessionInvalidationReason
{
    None,
    LoggedOut,
    Expired,
    ProfilePasswordChanged,
    CanonicalRecovered,
    ProfileRemoved
}
