namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Calculates when the UI may show or act on an advisory expiry warning.</summary>
public static class SessionExpiryWarningPolicy
{
    public static DateTimeOffset GetWarningDeadline(DateTimeOffset expiresAtUtc, TimeSpan warningLeadTime)
    {
        if (warningLeadTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(warningLeadTime));

        return expiresAtUtc.ToUniversalTime() - warningLeadTime;
    }

    public static bool IsWarningDue(
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        TimeSpan warningLeadTime) =>
        nowUtc.ToUniversalTime() >= GetWarningDeadline(expiresAtUtc, warningLeadTime);
}
