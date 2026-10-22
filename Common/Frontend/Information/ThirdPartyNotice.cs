namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record ThirdPartyNotice(
    string Id,
    string Name,
    string Version,
    string LicenseExpression,
    string CopyrightNotice,
    Uri SourceUri,
    ThirdPartyPlatform Platforms,
    IReadOnlyList<ThirdPartyPackage> Packages,
    string? RequiredNotice,
    string LicenseText);
