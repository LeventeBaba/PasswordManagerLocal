namespace PasswordManagerLocal.Windows.Ipc.Contracts.Errors;

public enum IpcErrorCategory
{
    Protocol = 1,
    Validation = 2,
    Cancellation = 3,
    Availability = 4,
    Conflict = 5,
    Internal = 6
}
