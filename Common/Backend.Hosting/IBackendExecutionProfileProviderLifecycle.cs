namespace PasswordManagerLocal.Common.Backend.Hosting;

internal interface IBackendExecutionProfileProviderLifecycle
{
    void InitializeCurrent();
    void OpenEnrollmentAdmission();
    void CloseEnrollmentAdmission();
    void StopPublishing();
}
