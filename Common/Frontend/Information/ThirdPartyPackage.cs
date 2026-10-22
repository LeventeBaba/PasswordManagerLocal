namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record ThirdPartyPackage(
    string Name,
    string Version,
    Uri PackagePageUri,
    Uri SourceUri);
