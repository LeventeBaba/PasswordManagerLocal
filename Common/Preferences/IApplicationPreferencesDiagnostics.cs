namespace PasswordManagerLocal.Common.Preferences;

public interface IApplicationPreferencesDiagnostics
{
    void ReportFailure(string operation, Exception exception);
}
