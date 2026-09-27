namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserLoginIdentityMatchState : byte
{
    Matched = 0,
    NotFound = 1,
    Ambiguous = 2,
    ProjectionQuarantined = 3,
    ProjectionOutdated = 4,
    DeletedAccount = 5,
    InvalidProjection = 6
}
