namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

[Flags]
public enum UserDataBlobKind
{
    None = 0,
    General = 1,
    Passwords = 2,
    Devices = 4,
    All = General | Passwords | Devices
}
