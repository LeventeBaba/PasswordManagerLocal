namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IUiPreferencesDiagnostics
{
    void ReportFailure(
        UiPreferenceKind preference,
        UiPreferenceFailureStage stage,
        Type exceptionType);
}
