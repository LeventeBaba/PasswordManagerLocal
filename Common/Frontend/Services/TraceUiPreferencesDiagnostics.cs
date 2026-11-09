using System.Diagnostics;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class TraceUiPreferencesDiagnostics : IUiPreferencesDiagnostics
{
    public void ReportFailure(
        UiPreferenceKind preference,
        UiPreferenceFailureStage stage,
        Type exceptionType) =>
        Trace.TraceError(
            "A UI preference operation failed. Preference={0}; Stage={1}; ExceptionType={2}",
            preference,
            stage,
            exceptionType.Name);
}
