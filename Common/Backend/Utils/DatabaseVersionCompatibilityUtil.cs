using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class DatabaseVersionCompatibilityUtil
{
    public static bool IsIncomingDatabaseVersionSupported(int databaseVersion) =>
        databaseVersion >= DatabaseConstants.OldestSupportedDbVersion &&
        databaseVersion <= DatabaseConstants.CurrentDbVersion;

    public static string BuildIncomingDatabaseVersionUnsupportedMessage(int databaseVersion) =>
        new DatabaseVersionNotSupportedException(
            databaseVersion,
            DatabaseConstants.OldestSupportedDbVersion,
            DatabaseConstants.CurrentDbVersion,
            BuildUnsupportedReason(databaseVersion)).Message;

    private static string BuildUnsupportedReason(int databaseVersion)
    {
        if (databaseVersion < DatabaseConstants.OldestSupportedDbVersion)
            return "The remote device's database version is older than this device supports. Update the remote device before continuing.";

        if (databaseVersion > DatabaseConstants.CurrentDbVersion)
            return "The remote device's database version is newer than this device supports. Update this device before continuing.";

        return "The remote device's database version is incompatible with this device.";
    }
}
