using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class DeviceWriteConflict
{
    public static bool IsStaleDevice(Exception exception) =>
        exception is StorageWriteException { Kind: StorageWriteException.FailureKind.StaleRow } conflict &&
        conflict.RowType == typeof(Device);
}
