namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record PrivacyPolicyDocument(
    string Version, DateOnly ReviewedOn, DateOnly? EffectiveOn, bool IsDraft,
    string Title, string Summary, string StatusText, IReadOnlyList<PrivacyPolicySection> Sections);
