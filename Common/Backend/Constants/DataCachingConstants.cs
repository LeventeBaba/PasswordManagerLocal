namespace PasswordManagerLocal.Common.Backend.Constants;

public static class DataCachingConstants
{
    public static readonly TimeSpan UserDataCacheExpirationTime = TokenConstants.LoginTokenExpirationTime;
    public static readonly TimeSpan GroupDataCacheExpirationTime = TokenConstants.LoginTokenExpirationTime;
}
