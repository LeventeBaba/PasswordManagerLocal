namespace PasswordManagerLocal.Common.Backend.Constants;

public static class EntryExpirationConstants
{
    public static readonly TimeSpan PurgeExpiredPeriod = TimeSpan.FromMinutes(5); // period of the hosted service for purging expired entries
}