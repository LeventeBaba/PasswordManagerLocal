namespace PasswordManagerLocal.Common.Frontend.Localization;

public readonly record struct FrontendLocalizationChangeResult(
    FrontendLocalizationChangeStatus Status)
{
    public bool IsSuccess =>
        Status is FrontendLocalizationChangeStatus.Applied or
            FrontendLocalizationChangeStatus.Unchanged;
}
