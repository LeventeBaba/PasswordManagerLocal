namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserControlOperationType : byte
{
    KeyEpochReplacement = 1,
    MembershipChange = 2,
    DeviceAddition = 3,
    DeviceRemoval = 4,
    AccountDeletion = 5
}
