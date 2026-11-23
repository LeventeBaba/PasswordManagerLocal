using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class SqliteCapacityError
{
    public const string Marker = "pml_capacity:";

    public static bool IsCapacityFailure(SqliteException exception) =>
        exception.SqliteErrorCode is 13 or 18 ||
        (exception.SqliteErrorCode == 19 && exception.Message.Contains(Marker, StringComparison.Ordinal));

    public static StorageCapacityExceededException Wrap(SqliteException exception) => new(exception);
}
