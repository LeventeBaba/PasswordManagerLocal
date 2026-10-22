namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed record HelpSectionViewModel(string Heading, string Body, string Label, bool IsWarning, bool IsTip)
{
    public bool HasLabel => Label.Length != 0;
}
