namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record PrivacyPolicySection(string Id, string Heading, string Body)
{
    public bool IsRightsSection => Id == "rights";
    public bool ShowsDeveloperContact => Id is "diagnostics" or "rights";
}
