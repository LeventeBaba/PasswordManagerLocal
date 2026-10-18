namespace PasswordManagerLocal.Common.Frontend.Theming;

public readonly record struct AppThemeChangeResult(AppThemeChangeStatus Status)
{
    public bool IsSuccess => Status is AppThemeChangeStatus.Applied or AppThemeChangeStatus.Unchanged;
}
