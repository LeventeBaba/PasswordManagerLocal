namespace PasswordManagerLocal.Common.Backend.Models;

public enum SyncModelType : byte
{
    User = 1,
    // Value 2 was used by a pre-release model and is reserved for persisted data.
    Device = 3,
    UserDevice = 4
}
