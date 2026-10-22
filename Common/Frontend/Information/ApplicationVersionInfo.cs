using System.Reflection;

namespace PasswordManagerLocal.Common.Frontend.Information;

public static class ApplicationVersionInfo
{
    public static string? GetDisplayVersion(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(ApplicationVersionInfo).Assembly;
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
}
