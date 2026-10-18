using System.Diagnostics;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public sealed class TraceFrontendLocalizationDiagnostics : IFrontendLocalizationDiagnostics
{
    public void ReportLoadFailure(
        AppLanguage language,
        FrontendLocalizationFailureKind failureKind,
        Type exceptionType) =>
        Trace.TraceError(
            "Frontend localization load failed. Language={0}; Failure={1}; ExceptionType={2}",
            language,
            failureKind,
            exceptionType.Name);

    public void ReportMissingKey(AppLanguage? language) =>
        Trace.TraceWarning(
            "A frontend localization key was unavailable. Language={0}",
            language?.ToString() ?? "none");
}
