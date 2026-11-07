using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class DeviceWriteConflict
{
    public static bool IsStaleDevice(Exception exception) =>
        exception switch
        {
            StorageWriteException { Kind: StorageWriteException.FailureKind.StaleRow, RowType: var type } =>
                type == typeof(Device),
            DbUpdateConcurrencyException ef => ef.Entries.Count > 0 && ef.Entries.All(entry => entry.Entity is Device),
            _ => false
        };
}
