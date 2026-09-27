namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserLoginIdentityStatus : byte
{
    Active = 0,
    IntegrityConflict = 1,
    InvalidSource = 2
}
