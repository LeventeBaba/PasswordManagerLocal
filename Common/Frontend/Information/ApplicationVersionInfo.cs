using System.Globalization;
using System.Reflection;

namespace PasswordManagerLocal.Common.Frontend.Information;

public static class ApplicationVersionInfo
{
    public static string? GetDisplayVersion(Assembly? assembly = null)
    {
        assembly ??= typeof(ApplicationVersionInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Trim();
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator > 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        var version = assembly.GetName().Version;
        if (version is null)
            return null;

        return version.Revision > 0
            ? version.ToString(4)
            : version.Build > 0
                ? version.ToString(3)
                : version.ToString(2);
    }

    internal static DateTime? GetBuildTimestampUtc()
    {
        var timestamp = typeof(ApplicationVersionInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimestampUtc")?.Value;

        return DateTime.TryParseExact(
            timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && parsed.Kind == DateTimeKind.Utc
            ? parsed
            : null;
    }
}
