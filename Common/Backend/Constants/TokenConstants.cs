namespace PasswordManagerLocal.Common.Backend.Constants;

public static class TokenConstants
{
    public const int LoginTokenExpirationMinutes = 30;
    public const int InvalidationReasonRetentionMinutes = LoginTokenExpirationMinutes;

    public static readonly TimeSpan LoginTokenExpirationTime = TimeSpan.FromMinutes(LoginTokenExpirationMinutes);
    public static readonly TimeSpan InvalidationReasonRetentionTime = TimeSpan.FromMinutes(InvalidationReasonRetentionMinutes);

    public const int NumberOfTokenGenerationRetries = 1;
}
