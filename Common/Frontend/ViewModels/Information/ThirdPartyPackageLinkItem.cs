namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed record ThirdPartyPackageLinkItem(
    string DisplayName,
    Uri PackagePageUri,
    Uri SourceUri);
