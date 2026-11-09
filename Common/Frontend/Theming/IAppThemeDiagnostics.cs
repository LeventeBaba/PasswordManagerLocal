namespace PasswordManagerLocal.Common.Frontend.Theming;

public interface IAppThemeDiagnostics
{
    void ReportFailure(
        AppThemeId theme,
        AppThemeFailureKind failureKind,
        Type exceptionType);
}
