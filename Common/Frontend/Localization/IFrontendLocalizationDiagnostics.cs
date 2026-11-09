using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public interface IFrontendLocalizationDiagnostics
{
    void ReportLoadFailure(
        AppLanguage language,
        FrontendLocalizationFailureKind failureKind,
        Type exceptionType);

    void ReportMissingKey(AppLanguage? language);
}
