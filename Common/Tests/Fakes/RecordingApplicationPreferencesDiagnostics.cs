using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class RecordingApplicationPreferencesDiagnostics : IApplicationPreferencesDiagnostics
{
    private readonly List<(string Operation, Type ExceptionType)> _failures = [];

    public IReadOnlyList<(string Operation, Type ExceptionType)> Failures => _failures;

    public void ReportFailure(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        _failures.Add((operation, exception.GetType()));
    }
}
