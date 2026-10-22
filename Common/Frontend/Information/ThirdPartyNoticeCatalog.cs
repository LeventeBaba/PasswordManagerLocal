namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record ThirdPartyNoticeCatalog(
    int SchemaVersion,
    DateOnly ReviewedDate,
    IReadOnlyList<ThirdPartyNotice> Notices);
