namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserSyncKeyConfidence : byte
{
    UnconfirmedPassword = 0,
    AuthenticatedSession = 1,
    RememberMe = 2,
    VerifiedRemoteSnapshot = 3,
    ExplicitlyTrusted = 4
}
