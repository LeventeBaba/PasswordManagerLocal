namespace PasswordManagerLocal.Common.Frontend.Information;

/// <summary>A public-document destination. Never contains account data or form values.</summary>
public sealed record InformationRoute(InformationDestination Destination, string? ContentId = null);
