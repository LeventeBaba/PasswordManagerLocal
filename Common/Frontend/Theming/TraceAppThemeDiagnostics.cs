using System.Diagnostics;

namespace PasswordManagerLocal.Common.Frontend.Theming;

public sealed class TraceAppThemeDiagnostics : IAppThemeDiagnostics
{
    public void ReportFailure(
        AppThemeId theme,
        AppThemeFailureKind failureKind,
        Type exceptionType) =>
        Trace.TraceError(
            "Frontend theme operation failed. Theme={0}; Failure={1}; ExceptionType={2}",
            theme,
            failureKind,
            exceptionType.Name);
}
