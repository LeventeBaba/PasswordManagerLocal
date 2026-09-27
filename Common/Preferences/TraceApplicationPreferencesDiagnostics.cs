using System.Diagnostics;

namespace PasswordManagerLocal.Common.Preferences;

public sealed class TraceApplicationPreferencesDiagnostics : IApplicationPreferencesDiagnostics
{
    public void ReportFailure(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError(
            "Application preferences operation '{0}' failed with {1}.",
            operation,
            exception.GetType().Name);
    }
}
